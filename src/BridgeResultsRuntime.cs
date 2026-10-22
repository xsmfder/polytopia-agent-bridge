using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;

namespace PolytopiaAgent
{
    public partial class BridgeMod
    {
        private readonly Dictionary<string, string> _nativeFinalResults = new Dictionary<string, string>(StringComparer.Ordinal);
        internal const int ResultsMemoryCapacity = 32;
        private string _nativeFinalLatestEpisode;
        private string _resultsDirectoryOverride;
        private Type _resultsPlayerExtensionsType;

        private string ResultsDirectory
        { get { return _resultsDirectoryOverride ?? Path.Combine(WorkDir, "results"); } }

        // Called only by the natural MatchEnded callback, before its EndSession call.
        // The callback's ScoreDetails was calculated by EndMatchReaction for LocalPlayer.
        // A phase==Ended query alone is deliberately never enough to manufacture a final result.
        private void CaptureNativeFinalScore(object gs, byte controlled, object scoreDetails, bool won, byte? winner)
        {
            if (gs == null || !CRSameNative(gs, GameStateInstance()) || !PrivateSamplingAllowed(gs)) return;
            var identity = ResolveControlledIdentity(gs);
            if (!identity.Verified || identity.Id != controlled || controlled == 0 || controlled == 255) return;
            string episode = EpisodeId(gs);
            if (!ResultsEpisodeSafe(episode)) return;
            var settings = MemberWithAccessor(gs, "Settings");
            string mode = SafeScalar(MemberWithAccessor(settings, "RulesGameMode"));
            object rating = null;
            if (mode == "Domination")
                rating = ResultsComputeRating(_resultsPlayerExtensionsType ?? FindGameType("PlayerExtensions"), PlayerStateOf(gs, controlled), gs, won);
            string payload = ResultsPayload(mode, scoreDetails, rating, mode == "Domination" ? (bool?)won : null);
            bool complete = ResultsPayloadComplete(mode, scoreDetails, rating);
            var record = new JsonObj().Str("schema", "native-final-1").Str("episodeId", episode)
                .Raw("controlledPlayerId", controlled.ToString(CultureInfo.InvariantCulture))
                .NullOrStr("mode", mode).NullOrStr("gameType", SafeScalar(MemberWithAccessor(settings, "GameType")))
                .Raw("finalTurn", ResultsNumber(MemberWithAccessor(gs, "CurrentTurn")))
                .Bool("final", true).Bool("complete", complete).Bool("controlledPlayerWon", won)
                .Raw("winnerId", winner.HasValue && winner.Value != 0 && winner.Value != 255 ? winner.Value.ToString(CultureInfo.InvariantCulture) : "null")
                .Str("authority", "natural GameManager.MatchEnded; EndMatchReaction ScoreDetails; ResultScreen GetRating(localPlayerIsWinner)")
                .Str("capturedAtUtc", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)).Raw("scores", payload).Close();
            ResultsRememberFinal(episode, record);
        }

        // Routing may call this from menus. An active new episode never falls back to an old
        // episode; menu retrieval and explicit archival retrieval identify their original episode.
        private string ResultPublic(string episode = null)
        {
            var gs = GameStateInstance();
            string current = gs == null ? null : EpisodeId(gs);
            string requested = string.IsNullOrEmpty(episode) ? current : episode;
            if (string.IsNullOrEmpty(requested))
            {
                requested = _nativeFinalLatestEpisode;
                if (requested == null)
                {
                    string latest = ResultsReadFile(Path.Combine(ResultsDirectory, "latest.json"), null);
                    if (latest != null)
                    {
                        requested = Json.GetString(latest, "episodeId");
                        _nativeFinalLatestEpisode = requested; _nativeFinalResults[requested] = latest;
                        ResultsTrimMemory();
                    }
                }
            }
            if (!ResultsEpisodeSafe(requested)) return ResultsUnavailable("result-1", "no captured native final result", requested);
            string record;
            if (!_nativeFinalResults.TryGetValue(requested, out record))
                record = ResultsReadFile(Path.Combine(ResultsDirectory, requested + ".json"), requested);
            if (record == null) return ResultsUnavailable("result-1", "no native final breakdown captured for this episode; historical details are not reconstructed", requested);
            if (_fixedControlledSeat.HasValue && Json.GetString(record, "controlledPlayerId") != _fixedControlledSeat.Value.ToString(CultureInfo.InvariantCulture))
                return ResultsUnavailable("result-1", "captured result belongs to a different controlled seat", requested);
            return new JsonObj().Bool("ok", true).Str("schema", "result-1").Str("episodeId", requested)
                .Bool("isCurrentEpisode", current != null && current == requested).Bool("archived", current == null || current != requested)
                .Str("status", Json.GetString(record, "complete") == "true" ? "complete" : "partial")
                .Raw("result", record).Close();
        }

        // Current score only: no winner inference, no durable final record, no stale fallback.
        private string ScoreSnapshotPublic()
        {
            var gs = GameStateInstance();
            if (gs == null) return ResultsUnavailable("score-snapshot-1", "no active game", null);
            if (!PrivateSamplingAllowed(gs)) return ResultsUnavailable("score-snapshot-1", "controlled seat is not currently observable", null);
            var identity = ResolveControlledIdentity(gs);
            if (!identity.Verified) return ResultsUnavailable("score-snapshot-1", "controlled identity unavailable", null);
            var settings = MemberWithAccessor(gs, "Settings");
            string mode = SafeScalar(MemberWithAccessor(settings, "RulesGameMode"));
            var player = PlayerStateOf(gs, identity.Id);
            Type extensions = _resultsPlayerExtensionsType ?? FindGameType("PlayerExtensions");
            object score = mode == "Perfection" ? ResultsComputeScore(extensions, player, gs) : null;
            // Domination's full rating belongs to the terminal UI. Do not publish a
            // provisional wipe percentage calculated from non-disclosed opponents.
            object rating = null;
            return new JsonObj().Bool("ok", true).Str("schema", "score-snapshot-1").Str("episodeId", EpisodeId(gs))
                .Raw("controlledPlayerId", identity.Id.ToString(CultureInfo.InvariantCulture)).NullOrStr("mode", mode)
                .Raw("turn", ResultsNumber(MemberWithAccessor(gs, "CurrentTurn"))).Bool("final", false)
                .Str("status", mode == "Domination" ? "unavailable_before_terminal" : "provisional").Bool("complete", ResultsPayloadComplete(mode, score, rating))
                .Str("authority", "native PlayerExtensions projection of current controlled player; score uses an independent player/cache copy")
                .Str("settlement", "current snapshot; pending actions may still change scores")
                .Raw("scores", ResultsPayload(mode, score, rating, mode == "Domination" ? (bool?)false : null)).Close();
        }

        internal static object ResultsComputeScore(Type extensions, object player, object gs)
        {
            if (player == null || gs == null) return null;
            // Native GetUnlockedTech refreshes player.unlockedTechCache, and GetScore removes
            // root technologies from that returned list. Never supply the live PlayerState.
            object copy = ResultsScorePlayerCopy(player);
            if (copy == null) return null;
            var method = FindStaticUnique(extensions, "GetScore", new[] { "PlayerState", "GameState" }, "ScoreDetails");
            try { return method == null ? null : method.Invoke(null, new[] { copy, gs }); }
            catch (Exception error) { RecordNativeReadFailure(method.DeclaringType, method.Name, error); return null; }
        }

        internal static object ResultsComputeRating(Type extensions, object player, object gs, bool receivesTimeScore)
        {
            if (player == null || gs == null) return null;
            var method = FindStaticUnique(extensions, "GetRating", new[] { "PlayerState", "GameState", "Boolean" }, "PlayerRating");
            try { return method == null ? null : method.Invoke(null, new[] { player, gs, (object)receivesTimeScore }); }
            catch (Exception error) { RecordNativeReadFailure(method.DeclaringType, method.Name, error); return null; }
        }

        internal static object ResultsScorePlayerCopy(object player)
        {
            try
            {
                object copy = Activator.CreateInstance(player.GetType());
                foreach (string name in new[] { "Id", "tribe" })
                {
                    object value;
                    if (!TryReadNativeMember(player, name, out value) || value == null || SetMember(copy, name, value) == null) return null;
                }
                object tech, cache, originalCache;
                if (!TryReadNativeMember(player, "availableTech", out tech)
                    || !TryReadNativeMember(copy, "unlockedTechCache", out cache)
                    || !TryReadNativeMember(player, "unlockedTechCache", out originalCache)) return null;
                if (tech == null || cache == null) return null;
                object techCopy = Activator.CreateInstance(tech.GetType());
                object cacheCopy = Activator.CreateInstance(cache.GetType());
                var add = ResultsListAdd(techCopy.GetType());
                var items = ReadNativeList(tech);
                if (add == null || items == null) return null;
                int budget = 4096;
                foreach (object item in items)
                { if (--budget < 0 || item == null) return null; add.Invoke(techCopy, new[] { item }); }
                if (SetMember(copy, "availableTech", techCopy) == null || SetMember(copy, "unlockedTechCache", cacheCopy) == null) return null;
                if (CRSameNative(copy, player) || CRSameNative(techCopy, tech) || CRSameNative(cacheCopy, originalCache)) return null;
                return copy;
            }
            catch { return null; }
        }

        private static MethodInfo ResultsListAdd(Type type)
        {
            MethodInfo hit = null;
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                if (method.Name == "Add" && method.GetParameters().Length == 1)
                { if (hit != null) return null; hit = method; }
            return hit;
        }

        internal static string ResultsNumber(object value)
        {
            if (value == null || value.GetType().IsEnum) return "null";
            // Keep integer/decimal precision and the actual floating-point type. Converting
            // everything through double silently changes large native scalar parameters.
            switch (Type.GetTypeCode(value.GetType()))
            {
                case TypeCode.SByte: case TypeCode.Byte:
                case TypeCode.Int16: case TypeCode.UInt16:
                case TypeCode.Int32: case TypeCode.UInt32:
                case TypeCode.Int64: case TypeCode.UInt64:
                case TypeCode.Decimal:
                    return ((IFormattable)value).ToString(null, CultureInfo.InvariantCulture);
                case TypeCode.Single:
                    float single = (float)value;
                    return float.IsNaN(single) || float.IsInfinity(single) ? "null" : single.ToString("R", CultureInfo.InvariantCulture);
                case TypeCode.Double:
                    double number = (double)value;
                    return double.IsNaN(number) || double.IsInfinity(number) ? "null" : number.ToString("R", CultureInfo.InvariantCulture);
                default:
                    return "null";
            }
        }

        private static readonly string[] ResultsScoreFields = { "territoryScore", "cultureScore", "cityScore", "techScore", "totalScore", "difficultyBonusMultiplier", "difficultyBonus", "TotalScoreIncludingBonus" };
        private static readonly string[] ResultsRatingFields = { "battleScore", "wipeScore", "timeGoal", "timeScore", "difficultyScore", "averageRating" };

        internal static bool ResultsPayloadComplete(string mode, object score, object rating)
        {
            object source = mode == "Perfection" ? score : mode == "Domination" ? rating : null;
            if (source == null) return false;
            foreach (string field in mode == "Perfection" ? ResultsScoreFields : ResultsRatingFields)
                if (ResultsNumber(MemberWithAccessor(source, field)) == "null") return false;
            return true;
        }

        internal static string ResultsPayload(string mode, object score, object rating, bool? receivesTimeScore)
        {
            var scoreRow = new JsonObj();
            var ratingRow = new JsonObj();
            foreach (string field in ResultsScoreFields)
                scoreRow.Raw(field, ResultsNumber(MemberWithAccessor(score, field)));
            foreach (string field in ResultsRatingFields)
                ratingRow.Raw(field, ResultsNumber(MemberWithAccessor(rating, field)));
            ratingRow.NullableBool("shouldReceiveTimeScore", receivesTimeScore)
                .Str("scoreUnit", "percent-points; 100 means 100 percent, not a 0..1 fraction")
                .Str("timeGoalUnit", "turns");
            scoreRow.Str("scoreUnit", "points").Str("difficultyBonusMultiplierUnit", "dimensionless native bonus multiplier");
            return new JsonObj().Raw("scoreDetails", mode == "Perfection" ? scoreRow.Close() : "null")
                .Raw("playerRating", mode == "Domination" ? ratingRow.Close() : "null")
                .NullOrStr("totalUnit", mode == "Perfection" ? "points" : mode == "Domination" ? "percent-points" : null)
                .Raw("nativeFinalTotal", mode == "Perfection" ? ResultsNumber(MemberWithAccessor(score, "TotalScoreIncludingBonus"))
                    : mode == "Domination" ? ResultsNumber(MemberWithAccessor(rating, "averageRating")) : "null").Close();
        }

        private void ResultsRememberFinal(string episode, string record)
        {
            if (!ResultsEpisodeSafe(episode) || !ResultsRecordValid(record, episode)) return;
            string previous;
            if (!_nativeFinalResults.TryGetValue(episode, out previous))
                previous = ResultsReadFile(Path.Combine(ResultsDirectory, episode + ".json"), episode);
            // A complete frozen record stays immutable even after memory eviction.
            if (previous != null && Json.GetString(previous, "complete") == "true") return;
            _nativeFinalResults[episode] = record;
            _nativeFinalLatestEpisode = episode;
            try
            {
                Directory.CreateDirectory(ResultsDirectory);
                ResultsAtomicFile(Path.Combine(ResultsDirectory, episode + ".json"), record);
                ResultsAtomicFile(Path.Combine(ResultsDirectory, "latest.json"), record);
            }
            catch (Exception failure)
            {
                Log("results persistence unavailable for " + episode + " (" + failure.GetType().Name
                    + "); frozen native result remains in memory");
            }
            ResultsTrimMemory();
        }

        // Disk is the durable archive; this is only a small read cache. Never drop
        // the latest result or the sole remaining copy after an I/O failure.
        private void ResultsTrimMemory()
        {
            if (_nativeFinalResults.Count <= ResultsMemoryCapacity) return;
            var episodes = new List<string>(_nativeFinalResults.Keys);
            episodes.Sort(StringComparer.Ordinal);
            foreach (string episode in episodes)
            {
                if (_nativeFinalResults.Count <= ResultsMemoryCapacity) break;
                if (episode == _nativeFinalLatestEpisode || !ResultsEpisodeSafe(episode)) continue;
                string record = _nativeFinalResults[episode];
                if (ResultsReadFile(Path.Combine(ResultsDirectory, episode + ".json"), episode) == record)
                    _nativeFinalResults.Remove(episode);
            }
        }

        private static void ResultsAtomicFile(string path, string text)
        {
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, text, new System.Text.UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(tmp, path, null);
            else File.Move(tmp, path);
        }

        private static bool ResultsEpisodeSafe(string episode)
        {
            if (episode == null || episode.Length != 19 || !episode.StartsWith("ep-", StringComparison.Ordinal)) return false;
            for (int i = 3; i < episode.Length; i++)
                if (!(episode[i] >= '0' && episode[i] <= '9' || episode[i] >= 'a' && episode[i] <= 'f')) return false;
            return true;
        }

        private static bool ResultsRecordValid(string record, string episode)
        {
            try
            {
                var parsed = BridgeJsonCodec.Object(record);
                object schema, ep, final, scores, complete;
                if (!parsed.TryGetValue("schema", out schema) || (schema as string) != "native-final-1"
                    || !parsed.TryGetValue("episodeId", out ep) || !ResultsEpisodeSafe(ep as string)
                    || episode != null && (ep as string) != episode
                    || !parsed.TryGetValue("final", out final) || !(final is bool) || !(bool)final
                    || !parsed.TryGetValue("scores", out scores) || !(scores is Dictionary<string, object>)
                    || !parsed.TryGetValue("complete", out complete) || !(complete is bool)) return false;
                if ((bool)complete)
                {
                    object modeValue, detailValue, total;
                    if (!parsed.TryGetValue("mode", out modeValue)) return false;
                    string mode = modeValue as string;
                    if (mode != "Perfection" && mode != "Domination") return false;
                    var payload = (Dictionary<string, object>)scores;
                    if (!payload.TryGetValue(mode == "Perfection" ? "scoreDetails" : "playerRating", out detailValue)
                        || !(detailValue is Dictionary<string, object>)
                        || !payload.TryGetValue("nativeFinalTotal", out total) || !ResultsJsonNumberReadable(total)) return false;
                    var details = (Dictionary<string, object>)detailValue;
                    foreach (string field in mode == "Perfection" ? ResultsScoreFields : ResultsRatingFields)
                    {
                        object value;
                        if (!details.TryGetValue(field, out value) || !ResultsJsonNumberReadable(value)) return false;
                    }
                }
                return true;
            }
            catch { return false; }
        }

        // JSON numbers are preserved as text by the codec. Check their numeric
        // shape/finite range only; do not round or rewrite archival score values.
        private static bool ResultsJsonNumberReadable(object value)
        {
            var number = value as BridgeJsonCodec.Number;
            double finite;
            return number != null && double.TryParse(number.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out finite)
                && !double.IsNaN(finite) && !double.IsInfinity(finite);
        }

        private static string ResultsReadFile(string path, string episode)
        {
            try
            {
                var file = new FileInfo(path);
                if (!file.Exists || file.Length > 65536) return null;
                string text = File.ReadAllText(path);
                return ResultsRecordValid(text, episode) ? text : null;
            }
            catch { return null; }
        }

        private static string ResultsUnavailable(string schema, string reason, string episode)
        { return new JsonObj().Bool("ok", false).Str("schema", schema).Str("status", "unavailable").NullOrStr("episodeId", episode).Str("reason", reason).Raw("result", "null").Close(); }
    }
}
