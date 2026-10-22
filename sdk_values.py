"""Small value parsers shared by the observation and SDK boundaries.

Only actual integers and decimal integer strings are accepted. In particular,
Python's bool subclass and float-to-int truncation are never game values.
"""
import re


def native_identifier(value):
    """An enum/member name supplied by a native menu, never a local type inventory.

    Native discovery certifies enum membership. This only rejects diagnostic type
    names, numeric values and malformed labels at the Python projection boundary.
    """
    return value if isinstance(value, str) and re.fullmatch(r"[A-Za-z_][A-Za-z0-9_]*", value) else None


def integer(value):
    if isinstance(value, bool) or not isinstance(value, (int, str)):
        raise ValueError("expected an integer")
    if isinstance(value, str) and re.fullmatch(r"[+-]?[0-9]+", value.strip()) is None:
        raise ValueError("expected a decimal integer string")
    return int(value)


def coordinates(value, *, parentheses_required=False):
    if not isinstance(value, str):
        raise ValueError("expected coordinate string")
    text = value.strip()
    opening, closing = text.startswith("("), text.endswith(")")
    if opening != closing or parentheses_required and not opening:
        raise ValueError("coordinates require matching parentheses")
    if opening:
        text = text[1:-1]
    parts = text.split(",")
    if len(parts) != 2:
        raise ValueError("coordinates require exactly two integers")
    result = [integer(part) for part in parts]
    if any(part < -2147483648 or part > 2147483647 for part in result):
        raise ValueError("coordinates must fit native Int32")
    return result
