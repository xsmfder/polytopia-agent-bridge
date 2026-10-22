using System;
using System.Collections.Generic;
using System.Globalization;

namespace PolytopiaAgent
{
    public partial class BridgeMod
    {
        private string _playerRuntimeEpisode, _playerRuntimeSeat;

        // Called only after private observation is allowed, or from a complete projected
        // frame. A human turn, curtain, temporary null state or recap pause is not a new scope.
        private void EnsurePlayerRuntimeScope(string episode, string seat)
        {
            byte player;
            if (string.IsNullOrEmpty(episode) || !byte.TryParse(seat, NumberStyles.None,
                CultureInfo.InvariantCulture, out player) || player == 0 || player == NaturePlayerId) return;
            seat = player.ToString(CultureInfo.InvariantCulture);
            if (_playerRuntimeEpisode == episode && _playerRuntimeSeat == seat) return;
            bool previous = _playerRuntimeEpisode != null;
            bool seatChanged = previous && _playerRuntimeSeat != seat;
            _playerRuntimeEpisode = episode; _playerRuntimeSeat = seat;

            // These hook observations are static and can outlive a runtime host. Release
            // obsolete native state even on the first valid scope. Preserve a callback
            // which has already captured evidence for this exact episode and seat.
            if (_publicResultEpisode != episode || _publicResultControlled != player)
            {
                _publicResultState = null; _publicResultEpisode = null;
                _publicResultWinner = null; _publicResultWon = null; _publicResultControlled = null;
            }
            var staleAlive = new List<byte>();
            foreach (var entry in _publicAlive)
                if (seatChanged || entry.Value == null || entry.Value.Episode != episode)
                    staleAlive.Add(entry.Key);
            foreach (var key in staleAlive) _publicAlive.Remove(key);
            if (!previous) return;

            // Drop references only. Never invoke Hide, respond to a choice, or pop a screen
            // during cleanup: pooled native UI objects may already belong to the new game.
            _publicDiplomacyActive.Clear(); _publicDiplomacyProof.Clear();
            _publicPeaceReceipts.Clear(); _publicResponseExpectations.Clear();
            _peaceResponseDisplays.Clear();
            _leaderboardCapture = _leaderboardSnapshot = null; ClearLeaderboardView();
            ClearTribeInfoView();
            _notificationScope = null; _notificationStream = Guid.NewGuid().ToString("N");
            _notificationId = 0; _publicNotifications.Clear();
            _lastNotification = null; _lastNotificationTick = -1;
            _requestSampleState = _requestPipelineState = null;
            InvalidateNativeSamples();
            // Combat tickets/journal reset when CRObserve accepts a complete new frame.
            // Natural final results are archives and deliberately survive this cleanup.
        }
    }
}
