from estate_actions.audit import repositories
from estate_actions.github import GitHub


def test_discovery_recognizes_personal_owners_without_a_hardcoded_name(monkeypatch):
    routes = []

    def request(self, method, route, body=None):
        return {"type": "User"}

    def pages(self, route):
        routes.append(route)
        if route.startswith("/user/repos"):
            return [{"owner": {"login": "someone-else"}, "full_name": "someone-else/private"}]
        return [{"full_name": "someone-else/public"}]

    monkeypatch.setattr(GitHub, "request", request)
    monkeypatch.setattr(GitHub, "pages", pages)
    assert repositories(GitHub("secret", "a/b"), ["someone-else"]) == [
        "someone-else/private",
        "someone-else/public",
    ]
    assert "/users/someone-else/repos" in routes
    assert not any(route.startswith("/orgs/") for route in routes)


def test_discovery_uses_org_endpoint_for_organizations(monkeypatch):
    routes = []
    monkeypatch.setattr(GitHub, "request", lambda *args: {"type": "Organization"})

    def pages(self, route):
        routes.append(route)
        return []

    monkeypatch.setattr(GitHub, "pages", pages)
    assert repositories(GitHub("secret", "a/b"), ["an-org"]) == []
    assert "/orgs/an-org/repos" in routes
