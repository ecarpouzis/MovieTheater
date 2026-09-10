"""The closed tag vocabulary the model passes emit against.

The `Insight` importer enforces the tag CATEGORY set but leaves values free, so
every pass that invents its own synonyms fragments the browse facets: a chip on
`sci-fi` misses the books tagged `science-fiction`, and `non-fiction` /
`nonfiction` split one shelf in two. This module is the single canonical map —
`canon_genre` folds a free-text genre onto the vocabulary the earlier inference
(`claude-opus-4-8`) established, and `derived_tags` adds the `setting` / `tone`
tags that follow deterministically from a genre, so an author-level pass reaches
the same tag density as a per-book one without a second model judgment.

Both are pure functions over strings: the emitter uses them when writing new
insights, and `normalize_tags.py` uses the same map to back-fill rows already in
the database. Keep them the only definition — a second copy drifts again.
"""

# Canonical genres. Anything not here is folded by GENRE_CANON below or dropped.
GENRE_ALLOWED = {
    # fiction shelves
    "adult-romance", "erotica", "romance", "paranormal-romance", "historical-romance",
    "contemporary-romance", "romantic-suspense", "chick-lit",
    "sci-fi", "military-sci-fi", "space-opera", "cyberpunk", "dystopian", "post-apocalyptic",
    "fantasy", "epic-fantasy", "urban-fantasy", "dark-fantasy", "sword-and-sorcery",
    "supernatural", "paranormal", "horror", "cosmic-horror", "zombies", "vampires",
    "mystery", "cozy-mystery", "detective", "crime", "noir", "hardboiled", "true-crime",
    "thriller", "spy", "legal-thriller", "suspense",
    "literary", "historical", "classic", "satire", "humor", "dark-comedy",
    "adventure", "western", "war", "action", "gothic", "magical-realism",
    "short-stories", "anthology", "poetry", "drama", "screenplay",
    "coming-of-age", "slice-of-life", "lgbt", "womens-fiction", "urban-fiction",
    "christian-fiction", "media-tie-in", "steampunk", "alternate-history",
    "mythology", "folklore", "fairy-tale-retelling", "pulp", "picture-book",
    # nonfiction shelves
    "non-fiction", "history", "biography", "autobiography", "science", "politics",
    "religion", "philosophy", "psychology", "self-help", "travel", "cooking",
    "art", "music", "film", "sports", "business", "reference", "essays", "journalism",
}

# Free-text genre -> canonical. Everything the model passes have actually emitted.
GENRE_CANON = {
    # the drift that split existing shelves
    "nonfiction": "non-fiction",
    "science-fiction": "sci-fi",
    "hard-science-fiction": "sci-fi",
    "science-fantasy": "sci-fi",
    "military-science-fiction": "military-sci-fi",
    "science-fiction-romance": "sci-fi",
    "literary-fiction": "literary",
    "historical-fiction": "historical",
    "political": "politics",
    "memoir": "autobiography",
    "autobiographical-fiction": "autobiography",
    "comedy": "humor",
    "dark-humor": "dark-comedy",
    "adult-humor": "humor",
    "parody": "satire",
    "jokes": "humor",
    "cartoons": "humor",
    "classics": "classic",
    # regional / period fiction -> the shelf a reader browses
    "victorian-fiction": "historical",
    "scottish-fiction": "literary",
    "australian-fiction": "literary",
    "southern-fiction": "literary",
    "rural-fiction": "literary",
    "realistic-fiction": "literary",
    "feminist-fiction": "literary",
    "translated-fiction": "literary",
    "experimental": "literary",
    "weird-fiction": "literary",
    "dark-fiction": "dark-fantasy",
    "southern-gothic": "gothic",
    "war-fiction": "war",
    "military-fiction": "war",
    "naval-fiction": "war",
    "sea-story": "adventure",
    "men-s-adventure": "action",
    "survival": "adventure",
    "weird-western": "western",
    "western-romance": "romance",
    "regency-romance": "historical-romance",
    "vintage-romance": "romance",
    "category-romance": "romance",
    "scottish-romance": "historical-romance",
    "time-travel-romance": "romance",
    "shifter-romance": "paranormal-romance",
    "motorcycle-club-romance": "adult-romance",
    "dark-romance": "adult-romance",
    "inspirational-romance": "christian-fiction",
    "new-adult": "romance",
    "romantic-comedy": "romance",
    "medical-fiction": "literary",
    "medical-thriller": "thriller",
    "psychological-thriller": "thriller",
    "psychological": "thriller",
    "techno-thriller": "thriller",
    "financial-thriller": "thriller",
    "historical-mystery": "mystery",
    "sherlock-holmes": "mystery",
    "espionage": "spy",
    "spy-fiction": "spy",
    "grimdark": "dark-fantasy",
    "splatterpunk": "horror",
    "extreme-horror": "horror",
    "creature-feature": "horror",
    "celtic-fantasy": "fantasy",
    "contemporary-fantasy": "urban-fantasy",
    "historical-fantasy": "fantasy",
    "faeries": "fantasy",
    "fantasy-world": "fantasy",
    "superhero": "action",
    "time-slip": "historical",
    "holiday": "romance",
    "manga": "media-tie-in",
    "doctor-who": "media-tie-in",
    "periodical": "anthology",
    "monologue": "drama",
    "play": "drama",
    "contemporary": "literary",
    # audience words that are not genres — the audience tag already carries them
    "young-adult": None,
    "childrens": None,
    "middle-grade": None,
    "bedtime": None,
    "fiction": None,
    "all-ages": None,
    # nonfiction subjects -> the nearest canonical shelf
    "military-history": "history",
    "oral-history": "history",
    "holocaust": "history",
    "archaeology": "history",
    "urban-studies": "history",
    "linguistics": "reference",
    "literary-criticism": "essays",
    "writing": "reference",
    "education": "reference",
    "law": "reference",
    "programming": "reference",
    "technology": "science",
    "physics": "science",
    "medicine": "science",
    "medical": "science",
    "health": "self-help",
    "nature": "science",
    "environment": "science",
    "animals": "science",
    "sociology": "politics",
    "economics": "business",
    "food": "cooking",
    "fashion": "art",
    "pop-culture": "art",
    "christian": "religion",
    "buddhism": "religion",
    "spirituality": "religion",
    "new-age": "religion",
    "occult": "religion",
    "activism": "politics",
    "relationships": "self-help",
    "parenting": "self-help",
}

# A genre implies a setting or a tone the earlier inference also wrote. Keeping
# these as rules (not model judgment) means the author pass reaches the same tag
# density without claiming knowledge of the individual book it does not have.
SETTING_BY_GENRE = {
    "epic-fantasy": "fantasy-world",
    "sword-and-sorcery": "fantasy-world",
    "dark-fantasy": "fantasy-world",
    "space-opera": "cosmic",
    "military-sci-fi": "cosmic",
    "cyberpunk": "future",
    "dystopian": "dystopian",
    "post-apocalyptic": "post-apocalyptic",
    "historical": "historical",
    "historical-romance": "historical",
    "western": "historical",
    "steampunk": "historical",
    "urban-fantasy": "urban",
    "urban-fiction": "urban",
    "noir": "urban",
    "contemporary-romance": "contemporary",
    "chick-lit": "contemporary",
}

# Tone is the sparse one on purpose. A rule earns its place only when it says
# something the genre does not: `erotica -> sensual` and `literary -> literary`
# are tautologies that would put 26k rows in the facet and make it useless, so
# they are deliberately absent.
TONE_BY_GENRE = {
    "noir": "dark",
    "hardboiled": "gritty",
    "dark-fantasy": "dark",
    "horror": "dark",
    "cosmic-horror": "dark",
    "gothic": "atmospheric",
    "zombies": "gritty",
    "satire": "satirical",
    "dark-comedy": "satirical",
    "epic-fantasy": "epic",
    "philosophy": "philosophical",
    "picture-book": "whimsical",
    "true-crime": "gritty",
}


def canon_genre(value):
    """Fold one free-text genre onto the vocabulary. Returns None to drop it."""
    if not value:
        return None
    v = value.strip().lower().replace(" ", "-").replace("_", "-").replace("'", "-")
    while "--" in v:
        v = v.replace("--", "-")
    if v in GENRE_ALLOWED:
        return v
    if v in GENRE_CANON:
        return GENRE_CANON[v]
    return None


def canon_genres(values):
    """Fold a list of genres, dropping duplicates and unknowns, order preserved."""
    out = []
    for value in values or ():
        g = canon_genre(value)
        if g and g not in out:
            out.append(g)
    return out


def derived_tags(genres):
    """The setting/tone tags a canonical genre list implies. At most one of each,
    taken from the first genre that carries a rule, so a book gets the setting of
    its primary shelf rather than every shelf it touches."""
    out = {}
    for g in genres:
        if "setting" not in out and g in SETTING_BY_GENRE:
            out["setting"] = [SETTING_BY_GENRE[g]]
        if "tone" not in out and g in TONE_BY_GENRE:
            out["tone"] = [TONE_BY_GENRE[g]]
    return out
