"""
ponytail: the one runnable check for the search cache -- what it keeps, what Oko pages through, and what a
forget leaves behind. No fixtures and no network. Run with `pytest`, or with `python test_cache.py` in the
pod image, which has no pytest.
"""

import tempfile

import cache

TRACK = "4cOdK2wGLETKBW3PvgPWqT"


def _dto(track_id=TRACK, name="Never Gonna Give You Up"):
    return {"id": "spotify://" + track_id, "name": name, "artist": "Rick Astley", "album": "Whenever You Need Somebody",
            "duration": "00:03:33", "thumbnailUrl": "640.jpg", "originalTitle": None, "originalArtist": None}


def test_first_seen_wins_and_survives_a_restart():
    with tempfile.TemporaryDirectory() as directory:
        songs = cache.Results(directory)
        songs.remember([_dto()])
        songs.remember([_dto(name="Renamed")])

        assert cache.Results(directory).get(TRACK) == _dto()
        assert songs.get("missing") is None
        assert songs.count() == 1


def test_pages_stay_pinned_to_what_page_one_saw():
    with tempfile.TemporaryDirectory() as directory:
        songs = cache.Results(directory)
        songs.remember([_dto(str(index)) for index in range(3)])

        first = songs.browse(None, 0, 2, None)
        songs.remember([_dto("late")])
        second = songs.browse(None, 2, 2, first["top"])

        assert [row["id"] for row in first["rows"]] == ["spotify://2", "spotify://1"]
        assert [row["id"] for row in second["rows"]] == ["spotify://0"]
        assert second["total"] == 3
        assert songs.browse(None, 0, 2, None)["total"] == 4


def test_search_folds_cyrillic_and_matches_the_id():
    with tempfile.TemporaryDirectory() as directory:
        songs = cache.Results(directory)
        songs.remember([_dto("a", "Тази песен не е за Любов"), _dto("b")])

        assert songs.browse("любов", 0, 10, None)["total"] == 1
        assert songs.browse("spotify://b", 0, 10, None)["rows"][0]["name"] == "Never Gonna Give You Up"
        assert songs.browse(None, -5, 0, None)["offset"] == 0


def test_forget():
    with tempfile.TemporaryDirectory() as directory:
        songs = cache.Results(directory)
        songs.remember([_dto()])

        assert songs.forget(TRACK)
        assert not songs.forget(TRACK)
        assert songs.get(TRACK) is None


if __name__ == "__main__":
    for name, test in sorted(dict(globals()).items()):
        if name.startswith("test_"):
            test()
            print(f"  {name} ok")

    print("selftest OK")
