import unittest

from discogs_match import agreed_fields, master_fields, norm_artist, plan_song_update, release_matches, track_credits


def release(id=1, artist="Henrik von Euler", title="Små Vågor 5", tracks=("Östermalm", "Vågor"),
            labels=(("Flora & Fauna", "FFLP43"),), year=2022, country="Sweden", extra=()):
    return {
        "id": id, "title": title, "year": year, "country": country,
        "artists": [{"name": artist, "join": ""}],
        "labels": [{"name": n, "catno": c} for n, c in labels],
        "tracklist": [{"position": f"A{i}", "type_": "track", "title": t,
                       "extraartists": [{"name": "Mixer (3)", "role": "Mixed By", "id": 9}] if i == 1 else []}
                      for i, t in enumerate(tracks, 1)],
        "extraartists": list(extra),
    }


class NormTests(unittest.TestCase):
    def test_strips_discogs_disambiguation_and_anv_star(self):
        self.assertEqual(norm_artist("Polisen (2)"), "polisen")
        self.assertEqual(norm_artist("Solulf*"), "solulf")


class MatchTests(unittest.TestCase):
    def test_exact_artist_title_and_all_tracks(self):
        self.assertTrue(release_matches(release(), "Henrik von Euler", "Små Vågor 5", ["Vågor"]))

    def test_rejects_missing_track(self):
        self.assertFalse(release_matches(release(), "Henrik von Euler", "Små Vågor 5", ["Other"]))

    def test_rejects_other_title_or_artist(self):
        self.assertFalse(release_matches(release(), "Henrik von Euler", "Små Vågor 4", ["Vågor"]))
        self.assertFalse(release_matches(release(), "Someone", "Små Vågor 5", ["Vågor"]))

    def test_various_artists_matches_discogs_various(self):
        self.assertTrue(release_matches(release(artist="Various"), "Various Artists", "Små Vågor 5", ["Vågor"]))

    def test_original_mix_means_plain_title(self):
        self.assertTrue(release_matches(release(), "Henrik von Euler", "Små Vågor 5", ["Vågor (Original Mix)"]))
        self.assertTrue(release_matches(release(), "Henrik von Euler", "Små Vågor 5", ["Vågor (Original)"]))

    def test_ampersand_equals_and(self):
        r = release(artist="Hercules And Love Affair*", title="Hercules And Love Affair")
        self.assertTrue(release_matches(r, "Hercules & Love Affair", "Hercules & Love Affair", ["Vågor"]))

    def test_store_suffix_and_invisible_characters(self):
        r = release(title="Freaky Tweaky Vol.4", tracks=("L'éclair",))
        self.assertTrue(release_matches(r, "Henrik von Euler", "Freaky Tweaky vol\u200b\u200b.\u200b4 - Single", ["L\u2019éclair"]))

    def test_artist_subset_either_way(self):
        split = release(artist="Schneider TM")
        split["artists"] = [{"name": "Schneider TM", "join": "/"}, {"name": "KPT.Michi.Gan", "join": ""}]
        self.assertTrue(release_matches(split, "Schneider TM", "Små Vågor 5", ["Vågor"]))
        self.assertTrue(release_matches(release(), "Henrik von Euler & Små Vågor", "Små Vågor 5", ["Vågor"]))
        self.assertFalse(release_matches(release(), "Små Vågor", "Små Vågor 5", ["Vågor"]))

    def test_keeps_version_words(self):
        r = release(tracks=("Upp ur sprickan (Komponisten Remix)",))
        self.assertFalse(release_matches(r, "Henrik von Euler", "Små Vågor 5", ["Upp ur sprickan"]))


class AgreementTests(unittest.TestCase):
    def test_single_release_gives_all_fields(self):
        self.assertEqual(agreed_fields([release()]),
                         {"label": "Flora & Fauna", "catno": "FFLP43", "year": 2022, "country": "Sweden"})

    def test_editions_keep_only_agreeing_fields(self):
        got = agreed_fields([release(), release(id=2, labels=(("Flora & Fauna", "FFCD43"),), country="Europe")])
        self.assertEqual(got, {"label": "Flora & Fauna", "year": 2022})

    def test_ignores_not_on_label_and_none_catno(self):
        got = agreed_fields([release(labels=(("Not On Label", "none"),))])
        self.assertNotIn("label", got)
        self.assertNotIn("catno", got)


class MasterTests(unittest.TestCase):
    def test_label_shared_by_every_edition_and_original_year(self):
        main = release(labels=(("Ninja Tune", "ZENCD195"),), year=2015)
        editions = [{"label": ["Ninja Tune", "Ninja Tune"]}, {"label": ["Beat Records", "Ninja Tune"]}]
        self.assertEqual(master_fields(main, {"year": 2013}, editions), {"label": "Ninja Tune", "year": 2013})

    def test_no_label_when_an_edition_lacks_it(self):
        main = release(labels=(("Ninja Tune", "ZENCD195"),))
        self.assertEqual(master_fields(main, {"year": 0}, [{"label": ["Other"]}]), {})


class CreditTests(unittest.TestCase):
    def test_track_and_release_wide_credits(self):
        r = release(extra=[{"name": "Writer (2)", "role": "Written-By", "tracks": "", "id": 4},
                           {"name": "Only B", "role": "Vocals", "tracks": "B1", "id": 5}])
        self.assertEqual(track_credits(r, "Östermalm"), [
            {"name": "Mixer", "role": "Mixed By", "discogsArtistId": 9},
            {"name": "Writer", "role": "Written-By", "discogsArtistId": 4}])

    def test_credits_must_agree_across_editions(self):
        a, b = release(), release(id=2, tracks=("Östermalm", "Vågor"))
        b["tracklist"][0]["extraartists"] = []
        self.assertIsNone(track_credits([a, b], "Östermalm"))


class PlanTests(unittest.TestCase):
    def test_fills_blanks_and_reports_conflicts(self):
        song = {"label": "", "catno": "OLD1", "year": None}
        sets, conflicts = plan_song_update(song, {"label": "Flora & Fauna", "catno": "FFLP43", "year": 2022})
        self.assertEqual(sets, {"label": "Flora & Fauna", "year": 2022})
        self.assertEqual(conflicts, {"catno": {"current": "OLD1", "discogs": "FFLP43"}})

    def test_same_value_case_insensitive_is_not_conflict(self):
        sets, conflicts = plan_song_update({"label": "flora & fauna"}, {"label": "Flora & Fauna"})
        self.assertEqual((sets, conflicts), ({}, {}))


if __name__ == "__main__":
    unittest.main()
