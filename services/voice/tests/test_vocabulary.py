import pytest

from voice.vocabulary import apply_vocabulary, parse_vocabulary


def test_parse_vocabulary_splits_trims_and_dedupes():
    assert parse_vocabulary("Christopher, Lots;  authentik\nChristopher, ,") == ["Christopher", "Lots", "authentik"]
    assert parse_vocabulary(None) == []
    assert parse_vocabulary("   ") == []


@pytest.mark.parametrize("heard, expected", [
    ("Hej, jag heter Christoffer. Vad heter du?", "Hej, jag heter Christopher. Vad heter du?"),  # the real case
    ("jag heter christoffer", "jag heter Christopher"),
    ("Kolla authentik och longhorn.", "Kolla Authentik och Longhorn."),                         # casing of product names
    ("Hello, my name is Christopher.", "Hello, my name is Christopher."),                       # already right
])
def test_close_words_and_casing_are_fixed(heard, expected):
    assert apply_vocabulary(heard, ["Christopher", "Authentik", "Longhorn"]) == expected


@pytest.mark.parametrize("heard", ["Christoffer", "Kristoffer", "Cristofer", "Christofer", "Kristofer"])
def test_sound_alike_spellings_of_a_name_are_fixed(heard):
    assert apply_vocabulary(f"Hej {heard}!", ["Christopher"]) == "Hej Christopher!"


@pytest.mark.parametrize("other", ["Christian", "Kristina", "Kristian", "Christmas", "Chris"])
def test_different_names_that_merely_look_similar_are_not_touched(other):
    assert apply_vocabulary(f"Hej {other}!", ["Christopher"]) == f"Hej {other}!"


def test_unrelated_words_are_left_alone():
    words = ["Christopher", "Authentik", "Lots"]
    text = "Jag sitter och dricker en vit Monster och är ganska trött. Vi har lots of time, men inte Christian."
    assert apply_vocabulary(text, words) == text.replace("lots", "Lots")  # only the exact word is recased
    assert apply_vocabulary("Christian och Kristina", ["Christopher"]) == "Christian och Kristina"


def test_short_words_are_only_fixed_on_exact_match():
    assert apply_vocabulary("Lars och Lots", ["Lots"]) == "Lars och Lots"
    assert apply_vocabulary("lots", ["Lots"]) == "Lots"
    assert apply_vocabulary("Lost", ["Lots"]) == "Lost"  # a close but different short word stays


def test_multi_word_entries_fix_casing_only():
    assert apply_vocabulary("starta om longhorn manager nu", ["Longhorn Manager"]) == "starta om Longhorn Manager nu"


def test_swedish_letters_and_punctuation_are_preserved():
    assert apply_vocabulary("Är det Christoffer? Ja, Christoffer!", ["Christopher"]) == "Är det Christopher? Ja, Christopher!"


def test_threshold_is_adjustable_and_empty_inputs_are_safe():
    assert apply_vocabulary("Christian", ["Christopher"]) == "Christian"
    assert apply_vocabulary("Christian", ["Christopher"], threshold=0.5) == "Christopher"  # a looser setting
    assert apply_vocabulary("", ["Christopher"]) == ""
    assert apply_vocabulary("text", []) == "text"
