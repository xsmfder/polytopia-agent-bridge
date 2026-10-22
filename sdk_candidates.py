"""Read current native menu rows without upgrading unavailable data into actions."""


def current_rows(domain, key, *, statuses=('ok', 'partial')):
    # Older bridge responses omitted some nested status fields. Preserve those
    # shapes, but an explicit failed/unknown/unavailable state is authoritative.
    # Keep source data intact: this selects proposals, not the diagnostic payload.
    if not isinstance(domain, dict) or domain.get('ok') is False:
        return ()
    if 'status' in domain and domain['status'] not in statuses:
        return ()
    rows = domain.get(key)
    return (row for row in rows if isinstance(row, dict)) if isinstance(rows, list) else ()
