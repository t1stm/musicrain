"""The parts of lirika that need no GPU and no model. `uv run python test_lirika.py`."""

import re
import tomllib
from pathlib import Path

import numpy as np

import sources
import lirika
from lirika import Line, Tuning, Word


def test_labels_go_and_repeats_expand():
    text = "Припев:\nедно две\nтри (х2)\n\nКуплет 2:\nчетири\n\nПрипев\n\nпет x3"
    assert lirika.normalize(text) == ["едно две", "три", "три", "четири", "едно две", "три", "три",
                                      "пет", "пет", "пет"]


def test_a_label_on_the_line_itself():
    assert lirika.normalize("Припев: едно\nдве\n\nтри\n\nПрипев х2") == ["едно", "две", "три"] + ["едно", "две"] * 2


def test_a_chorus_never_written_out_is_an_error():
    try:
        lirika.normalize("едно\n\nПрипев")
    except ValueError:
        return
    raise AssertionError("a bare chorus reference with nothing to copy must not be guessed")


def test_a_lone_repeat_marker_is_an_error():
    try:
        lirika.normalize("едно\n(x2)")
    except ValueError:
        return
    raise AssertionError("'(x2)' on its own line repeats something unclear")


def test_words_that_only_look_like_markers_stay():
    assert lirika.normalize("Тих 2\nHook, line and sinker\n[ar:Someone]\n[00:12.34]Chorus of birds") == [
        "Тих 2", "Hook, line and sinker", "Chorus of birds"]


def test_a_loop_is_dropped_but_a_repetitive_song_is_not():
    assert lirika._repetitive("you know " * 10)
    assert not lirika._repetitive("all that you touch all that you see all that you taste all you feel "
                                  "all that you love all that you hate all you distrust all you save")


def test_punctuation_is_written_one_way():
    assert lirika.tidy("Про-про ,пробвай се,про..про-пробвай ме.") == "Про-про, пробвай се, про...про-пробвай ме."
    assert lirika.tidy("върни се.. ") == "върни се..." and lirika.tidy("Хей…хей....") == "Хей...хей..."
    assert lirika.tidy("be my best,") == "be my best,"


def test_stamps_and_the_hundred_minute_boundary():
    assert lirika.stamp(0) == "[00:00.00]"
    assert lirika.stamp(61.237) == "[01:01.24]"
    assert lirika.stamp(5999.994) == "[99:59.99]"
    assert lirika.stamp(6000) == "[1:40:00.00]"


# stih's LrcParser.TimestampRegex, so a file lirika writes is checked against the reader that will read it.
_STIH = re.compile(r"\[(?:(\d+):)?(\d{1,2}):(\d{1,2})(?:[.:](\d{1,3}))?\]")


def test_written_lrc_reads_back_through_stihs_rules():
    lines = [Line("първи ред", 12.5, 15.0, -0.2), Line("втори", 15.2, 16.0, -0.2), Line("трети", 6100.0, 6101.0, -0.2)]
    track = sources.Track(["Песен [Remix]", "Pesen"], ["Изпълнител"], 6110.0, None, None, "Албум")
    written = lirika.lrc(lines, 6110.0, Tuning(), track, by="Някой", asr="asr/model", aligner="ctc/model")
    read = []
    for raw in written.splitlines():
        stamps = list(_STIH.finditer(raw))
        if not stamps:
            continue
        hours, minutes, seconds, fraction = stamps[-1].groups()
        at = int(hours or 0) * 3600 + int(minutes) * 60 + int(seconds) + int(fraction) / 100
        read.append((round(at, 2), raw[stamps[-1].end():]))
    assert written.splitlines()[:10] == ["[ti:Песен [Remix]]", "[ar:Изпълнител]", "[al:Албум]", "[length:101:50]",
                                         "[by:Някой]", "[re:musicrain/Lirika]", f"[ve:{lirika.VERSION}]",
                                         "[#:asr:asr/model]", "[#:aligner:ctc/model]", "[#:edited:no]"]
    # Read back as a text (--force re-times an .lrc's words), the tags are not lyrics.
    assert lirika.normalize(written) == ["първи ред", "втори", "трети"]
    assert read == [(12.5, "първи ред"), (15.2, "втори"), (16.0, ""), (6100.0, "трети"), (6101.0, "")], read


def test_tags_without_a_value_are_left_out():
    written = lirika.lrc([Line("а", 1.0, 2.0, -0.2)], 59.6, Tuning(), sources.Track(["Песен"], [], None, None, None))
    assert written.splitlines()[:5] == ["[ti:Песен]", "[length:01:00]", "[re:musicrain/Lirika]", f"[ve:{lirika.VERSION}]",
                                        "[#:edited:no]"]


def test_activity_finds_the_sung_parts():
    rate = lirika.RATE
    audio = np.zeros(rate * 10, np.float32)
    audio[rate * 2:rate * 4] = 0.5 * np.sin(np.arange(rate * 2) * 0.1)
    audio[int(rate * 4.1):rate * 5] = 0.5  # a 100 ms breath gap: bridged
    audio[rate * 8:int(rate * 8.1)] = 0.5  # 100 ms: too short to be a region
    regions = lirika.activity(audio, Tuning())
    assert len(regions) == 1 and abs(regions[0][0] - 1.9) < 0.03 and abs(regions[0][1] - 5.1) < 0.03, regions


def test_lines_break_at_pauses_and_length():
    words = [Word(f"w{index}", index * 0.3, index * 0.3 + 0.2, -0.1) for index in range(5)]
    words += [Word("after", 3.0, 3.3, -0.1)]
    assert [line.text for line in lirika.split(words, Tuning())] == ["w0 w1 w2 w3 w4", "after"]
    assert len(lirika.split(words, Tuning(max_words=2))) == 4


def test_a_stanza_typed_on_one_line_is_broken_at_its_pauses():
    short = [Word("a", 0.0, 0.3, -0.1), Word("b", 0.4, 0.7, -0.1)]
    assert [line.text for line in lirika.shaped("a, b", short, Tuning())] == ["a, b"]
    rap = [Word(f"w{index}", index * 0.25, index * 0.25 + 0.2, -0.1) for index in range(16)]
    assert len(lirika.shaped("…", rap, Tuning())) == 1
    # Four sung lines of seven words, a breath between each, typed as one: broken at the breaths.
    stanza = [Word(f"w{index}", index * 0.5 + (index // 7) * 0.6, index * 0.5 + (index // 7) * 0.6 + 0.4, -0.1)
              for index in range(28)]
    assert [len(line.text.split()) for line in lirika.shaped("…", stanza, Tuning())] == [7, 7, 7, 7]


def _sung(text: str, start: float) -> list[Word]:
    return [Word(word, start + index * 0.5, start + index * 0.5 + 0.4, -0.2) for index, word in enumerate(text.split())]


def test_sung_words_take_the_lyrics_lines_repeats_and_all():
    lyrics = ["ти си далече не те забравям", "припевът се пее три пъти тук"]
    # Whisper mishears a word or two; the chorus, written once, is sung twice; an ad-lib is in nobody's text.
    sung = (_sung("ти си далече не те забравя", 10) + _sung("припевът се пее три пъти тук", 20)
            + _sung("хей хей", 30) + _sung("припевът се пее три пъти", 40))
    lines = lirika.map_lines(sung, lyrics, Tuning(map_text=True))
    assert [(line.text, line.start) for line in lines] == [
        (lyrics[0], 10.0), (lyrics[1], 20.0), ("хей хей", 30.0), (lyrics[1], 40.0)], [(l.text, l.start) for l in lines]
    assert lines[2].flags == ["not in the lyrics"] and not lines[3].flags


def test_a_whisper_sign_off_is_dropped_only_when_it_matched_no_lyrics():
    bg = lirika.LANGUAGES["bg"]
    invented = Line("Обожавам Ти! Благодаря", 333.7, 338.3, -0.6, ["not in the lyrics"])
    sung = Line("Благодаря ти за всичко", 10.0, 12.0, -0.2)
    assert lirika.signed_off(invented, bg) and not lirika.signed_off(sung, bg)


def test_starts_snap_to_a_near_onset_only():
    regions = [(0.0, 20.0)]
    near = [Line("a", 10.0, 11.0, -0.1)]
    lirika.finish(near, regions, np.array([9.9, 15.0]), Tuning())
    assert near[0].start == 9.9 and "no onset" not in near[0].flags

    far = [Line("a", 10.0, 11.0, -0.1)]
    lirika.finish(far, regions, np.array([9.5]), Tuning())
    assert far[0].start == 10.0 and "no onset" in far[0].flags


def test_ends_hold_through_the_note_but_stop_before_the_next_line():
    regions = [(0.0, 12.0), (14.0, 16.0)]
    lines = [Line("held", 1.0, 3.0, -0.1), Line("next", 14.0, 15.0, -0.1)]
    lirika.finish(lines, regions, np.array([1.0, 14.0]), Tuning())
    assert lines[0].end == 12.0 and lines[1].end == 16.0


def test_names_match_across_scripts_and_suffixes():
    track = sources.Track(["Невиждани очи"], ["Цветелина Янева"], 202.0, None, None)
    assert sources.same_song(track, "Tsvetelina Yaneva", "Nevizhdani ochi (Remix)") >= sources.MATCH
    assert sources.same_song(track, "Цветелина Янева", "Друга песен") < sources.MATCH


def test_latin_names_go_back_to_cyrillic_for_the_search():
    assert sources.cyrillic("Andrea Edno") == "Андреа Едно"
    assert sources.cyrillic("Tsvetelina Yaneva Nevizhdani ochi") == "Цветелина Янева Невиждани очи"


def test_the_right_text_agrees_and_a_different_song_does_not():
    right = "ти си далече не те забравям все те търся и в любов изгарям сърцата ни не се разделят никога"
    drafted = "ти си далече не те забравя все те търся и любов изгарям сърцата не се разделя никога"
    other = "морето е синьо и вятърът духа над старите лодки в залива рано сутрин всеки ден"
    assert sources.agreement(drafted, right) > 0.6 > sources.agreement(drafted, other)
    assert sources.agreement("твърде кратко", right) is None


def test_textove_pages_parse():
    # The page's markup around placeholder words: no real lyrics in the repository.
    search = ('<li><div itemscope><a href="artist-song-tekst" class="gtm-Search" data-pos="0">'
              '<img src="x.svg" alt="Artist"><h3><span itemprop="name">Песен &amp; още</span></h3>'
              '<h4>Изпълнител</h4><meta itemprop="byArtist" content="Изпълнител">')
    hit = sources._TEXTOVE_HIT.search(search)
    assert (hit["slug"], hit["title"], hit["artist"]) == ("artist-song-tekst", "Песен &amp; още", "Изпълнител")

    page = ('<div id="maintxt"><span itemprop="description"><p>едно<br />две</p><p>три &amp; четири<br />'
            'пет</p></span>\n</div>')
    assert sources.textove_words(page) == "едно\nдве\n\nтри & четири\nпет"

    translated = '<div id="maintxt"><p>be my best<br />едно</p><p>Превод</p><p>бъди мой най-добър</p>\n</div>'
    assert sources.textove_words(translated) == "be my best\nедно"


def test_the_machine_files_differ_only_in_torchs_runtime():
    def common(path: Path):
        project = tomllib.loads(path.read_text(encoding="utf-8"))["project"]
        runtime = ("rocm", "triton")
        return repr({**project, "dependencies": [d for d in project["dependencies"] if not d.startswith(runtime)]})

    files = sorted(Path(__file__).parent.glob("pyproject*.toml"))  # the three examples and this machine's copy
    assert len({common(path) for path in files}) == 1, [path.name for path in files]


if __name__ == "__main__":
    tests = [value for name, value in sorted(globals().items()) if name.startswith("test_")]
    for test in tests:
        test()
    print(f"{len(tests)} passed")
