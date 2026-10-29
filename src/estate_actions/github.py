"""Bounded, paginated GitHub REST access; no checkout or execution of PR code."""

from __future__ import annotations

import json
import re
import urllib.error
import urllib.parse
import urllib.request
from dataclasses import dataclass

from estate_actions.data import Invalid, decode, mapping, sequence


@dataclass(frozen=True)
class GitHub:
    token: str
    repository: str
    api_url: str = "https://api.github.com"

    def __post_init__(self) -> None:
        if not self.token:
            raise Invalid("A GitHub token is required")
        if not re.fullmatch(r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+", self.repository):
            raise Invalid("Expected repository owner/name")
        if urllib.parse.urlsplit(self.api_url).scheme != "https":
            raise Invalid("GitHub API URL must use HTTPS")

    def request(self, method: str, route: str, body: object = None) -> object:
        data = None if body is None else json.dumps(body).encode("utf-8")
        request = urllib.request.Request(
            self.api_url.rstrip("/")
            + (route if route.startswith("/") else f"/repos/{self.repository}/{route}"),
            data=data,
            method=method,
            headers={
                "Authorization": f"Bearer {self.token}",
                "Accept": "application/vnd.github+json",
                "Content-Type": "application/json",
                "X-GitHub-Api-Version": "2022-11-28",
                "User-Agent": "rtkelly-labs-github-actions",
            },
        )
        try:
            with urllib.request.urlopen(request, timeout=60) as response:
                raw = response.read(20_000_001)
                if len(raw) > 20_000_000:
                    raise Invalid("GitHub response exceeds the evidence safety limit")
                return decode(raw.decode("utf-8"))
        except urllib.error.HTTPError as error:
            # Do not expose request bodies, tokens, or response content in public comments.
            raise Invalid(f"GitHub API returned HTTP {error.code}") from None
        except (urllib.error.URLError, TimeoutError) as error:
            raise Invalid("GitHub API connection failed") from error

    def pages(self, route: str) -> list[dict[str, object]]:
        rows: list[dict[str, object]] = []
        separator = "&" if "?" in route else "?"
        for page in range(1, 1001):
            values = sequence(self.request("GET", f"{route}{separator}per_page=100&page={page}"))
            rows.extend(mapping(value) for value in values)
            if len(values) < 100:
                return rows
        raise Invalid("GitHub pagination limit exceeded; evidence is incomplete")

    def pull(self, number: int) -> dict[str, object]:
        if number < 1:
            raise Invalid("PR number must be positive")
        return mapping(self.request("GET", f"pulls/{number}"))
