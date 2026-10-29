"""Validated JSON boundary shared by the CLI and GitHub client."""

from __future__ import annotations

import json
from typing import NoReturn


class Invalid(ValueError):
    """An input cannot establish a complete, current assessment."""


def mapping(value: object) -> dict[str, object]:
    if not isinstance(value, dict) or any(not isinstance(key, str) for key in value):
        raise Invalid("Expected a JSON object with string keys")
    return {str(key): item for key, item in value.items()}


def sequence(value: object) -> list[object]:
    if not isinstance(value, list):
        raise Invalid("Expected a JSON array")
    return list(value)


def string(value: object) -> str:
    if not isinstance(value, str):
        raise Invalid("Expected a string")
    return value


def integer(value: object) -> int:
    if type(value) is not int:
        raise Invalid("Expected an integer")
    return value


def decode(value: str) -> object:
    try:
        parsed: object = json.loads(value, object_pairs_hook=unique_pairs, parse_constant=invalid_constant)
    except json.JSONDecodeError as error:
        raise Invalid("Invalid JSON") from error
    return parsed


def unique_pairs(pairs: list[tuple[str, object]]) -> dict[str, object]:
    result: dict[str, object] = {}
    for key, value in pairs:
        if key in result:
            raise Invalid("Duplicate JSON keys are invalid")
        result[key] = value
    return result


def invalid_constant(value: str) -> NoReturn:
    raise Invalid(f"Non-finite JSON number is invalid: {value}")


def canonical(value: object) -> str:
    return json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=True, allow_nan=False)
