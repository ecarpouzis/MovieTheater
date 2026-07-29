using System.Collections.Generic;
using System.Linq;
using MovieTheater.Db;

namespace MovieTheater.Channels
{
    /// <summary>Known schedule strategies (stored in <see cref="Db.Channel.ScheduleStrategy"/>).</summary>
    public static class ScheduleStrategies
    {
        public const string WeightedShuffle = "WeightedShuffle";
        public const string SeededShuffle = "SeededShuffle";
        public const string Marathon = "Marathon";
        public const string NewestFirst = "NewestFirst";
        public const string EpisodeRoundRobin = "EpisodeRoundRobin";
    }

    /// <summary>A credit rule expressed by person display-name (resolved to ids by the catalog command).
    /// Names OR within the rule; rules AND across the channel (a pairing).</summary>
    public sealed class CreditNameRule
    {
        public List<string> Names { get; set; } = new();
        public CreditRole? Role { get; set; }
    }

    /// <summary>
    /// One code-defined channel. Genres and credits are held as names and resolved to ids by
    /// <c>ChannelCatalogCommand</c> at apply time; everything else is baked straight into the
    /// <see cref="Filter"/>. Fluent builders keep each catalog entry to a line or two.
    /// </summary>
    public sealed class ChannelDef
    {
        public string Key { get; }
        public string Name { get; private set; }
        public string? Description { get; private set; }
        public string? Group { get; private set; }
        public ContentKinds Kinds { get; private set; } = ContentKinds.Movies;
        public string Strategy { get; private set; } = ScheduleStrategies.WeightedShuffle;
        public string? RotationJson { get; private set; }
        public int? SeasonStartMonth { get; private set; }
        public int? SeasonStartDay { get; private set; }
        public int? SeasonEndMonth { get; private set; }
        public int? SeasonEndDay { get; private set; }

        public ChannelFilter Filter { get; } = new();
        public List<string> GenreNames { get; } = new();
        public bool GenreModeAll { get; private set; }
        public List<CreditNameRule> CreditNames { get; } = new();

        private ChannelDef(string key, string name) { Key = key; Name = name; }
        public static ChannelDef Def(string key, string name, string? desc = null, string? group = null)
            => new ChannelDef(key, name) { Description = desc, Group = group };

        public ChannelDef In(ContentKinds k) { Kinds = k; return this; }
        public ChannelDef Strat(string s) { Strategy = s; return this; }
        public ChannelDef Group_(string g) { Group = g; return this; }

        public ChannelDef Genre(params string[] names) { GenreNames.AddRange(names); return this; }
        public ChannelDef GenreAll(params string[] names) { GenreNames.AddRange(names); GenreModeAll = true; return this; }
        public ChannelDef Dir(params string[] names) { CreditNames.Add(new CreditNameRule { Names = names.ToList(), Role = CreditRole.Director }); return this; }
        public ChannelDef Star(params string[] names) { CreditNames.Add(new CreditNameRule { Names = names.ToList(), Role = CreditRole.Actor }); return this; }

        public ChannelDef Year(int? min, int? max) { Filter.YearMin = min; Filter.YearMax = max; return this; }
        public ChannelDef Mpaa(int maxId) { Filter.MaxMpaRatingId = maxId; return this; }

        public ChannelDef Tag(TagCategory c, params string[] vals) { Filter.Tags.Add(new TagRule { Category = c, Values = vals.ToList() }); return this; }
        public ChannelDef NotTag(TagCategory c, params string[] vals) { Filter.Tags.Add(new TagRule { Category = c, Values = vals.ToList(), Negate = true }); return this; }
        public ChannelDef Sub(params string[] v) => Tag(TagCategory.Subgenre, v);
        public ChannelDef Mood(params string[] v) => Tag(TagCategory.Mood, v);
        public ChannelDef Tone(params string[] v) => Tag(TagCategory.Tone, v);
        public ChannelDef Setting(params string[] v) => Tag(TagCategory.Setting, v);
        public ChannelDef Era(params string[] v) => Tag(TagCategory.Era, v);
        public ChannelDef Visual(params string[] v) => Tag(TagCategory.VisualStyle, v);
        public ChannelDef Franchise(params string[] v) => Tag(TagCategory.Franchise, v);
        public ChannelDef Content(params string[] v) => Tag(TagCategory.ContentDescriptor, v);
        public ChannelDef Occasion(params string[] v) => Tag(TagCategory.Occasion, v);
        public ChannelDef Keyword(params string[] v) => Tag(TagCategory.Keyword, v);

        /// <summary>Judged channel membership: the pool is the set of titles hand-curated onto this
        /// station (Channel tags written by load-channel-tags), not a facet formula. The station's
        /// identity lives in docs/channel-slate-2026-07.md; its regression canon in
        /// docs/channel-canon.json. Convention: the tag value is the channel's own catalog key.</summary>
        public ChannelDef Judged() => Tag(TagCategory.Channel, Key);

        public ChannelDef Cult(double min) { Filter.CultClassic = new FilterRange(min, null); return this; }
        public ChannelDef Surreal(double min) { Filter.Surrealism = new FilterRange(min, null); return this; }
        public ChannelDef Novelty(double min) { Filter.Novelty = new FilterRange(min, null); return this; }
        public ChannelDef Rewatch(double min) { Filter.Rewatchability = new FilterRange(min, null); return this; }
        public ChannelDef Energy(double min) { Filter.Energy = new FilterRange(min, null); return this; }
        public ChannelDef Intensity(double? min, double? max) { Filter.Intensity = new FilterRange(min, max); return this; }

        public ChannelDef Imdb(double? min, double? max = null) { Filter.ImdbRating = new FilterRange(min, max); return this; }
        public ChannelDef Tomato(int min) { Filter.Tomatometer = new FilterRange(min, null); return this; }
        public ChannelDef Popcorn(int min) { Filter.Popcornmeter = new FilterRange(min, null); return this; }
        public ChannelDef Popular(double? min, double? max = null) { Filter.Popularity = new FilterRange(min, max); return this; }
        public ChannelDef Votes(int min) { Filter.VoteCount = new FilterRange(min, null); return this; }
        public ChannelDef Runtime(int? min, int? max) { Filter.Runtime = new FilterRange(min, max); return this; }

        public ChannelDef Lang(params string[] v) { Filter.Languages.AddRange(v); return this; }
        public ChannelDef NotLang(params string[] v) { Filter.ExcludeLanguages.AddRange(v); return this; }
        public ChannelDef Country(params string[] v) { Filter.Countries.AddRange(v); return this; }
        public ChannelDef Path(params string[] v) { Filter.PathContains.AddRange(v); return this; }
        public ChannelDef MinViewers(int n) { Filter.MinViewers = n; return this; }
        public ChannelDef AddedWithin(int days) { Filter.AddedWithinDays = days; return this; }
        /// <summary>Rolling release window ("the last N years", re-evaluated at query time) — unlike
        /// <see cref="Year"/>, which bakes fixed years into the stored filter.</summary>
        public ChannelDef ReleasedWithin(int years) { Filter.ReleasedWithinYears = years; return this; }
        public ChannelDef AllowAdult() { Filter.ExcludeAdult = false; return this; }

        public ChannelDef Season(int sm, int sd, int em, int ed)
        { SeasonStartMonth = sm; SeasonStartDay = sd; SeasonEndMonth = em; SeasonEndDay = ed; return this; }
        public ChannelDef Rotate(string rotationJson) { RotationJson = rotationJson; return this; }
    }

    /// <summary>
    /// The code-defined channel catalog (Channels 2.0). Built bottom-up from the real library
    /// (docs/channels-catalog.csv): auteurs, stars, cult/horror/weird, the animation & TV hall of
    /// fame, genres, decades, international, seasonal. <c>ChannelCatalogCommand</c> upserts these by
    /// <see cref="ChannelDef.Key"/>. Per-user channels (Unseen / Watchlist) are seeded separately.
    /// </summary>
    public static class ChannelCatalog
    {
        private const ContentKinds M = ContentKinds.Movies;
        private const ContentKinds MT = ContentKinds.Movies | ContentKinds.Series;
        private const ContentKinds T = ContentKinds.Series;
        private static ChannelDef D(string k, string n, string? d, string g) => ChannelDef.Def(k, n, d, g);

        /// <summary>
        /// Reserved <see cref="TagCategory.Channel"/> values that are NOT channels — they mark a title as
        /// belonging to one holiday and one holiday only. A locked title is excluded from every channel
        /// without a season window, all year (Eric, 2026-07: "we don't want a Christmas or Halloween-SPECIFIC
        /// movie showing on another channel"), which is why the judgment lives on the TITLE and not on
        /// seasonal-channel membership. The bar is specificity, not setting: A Charlie Brown Christmas and
        /// Hocus Pocus are locked; Gremlins and Die Hard merely happen in the season and stay everywhere.
        /// <para>Written by <c>load-channel-tags</c> like any membership; consumed by
        /// <c>ChannelScheduleService.AiFilters</c> via <see cref="ChannelFilter.AllowHolidayLocked"/>.</para>
        /// </summary>
        public static readonly List<string> HolidayLockKeys = new() { "lock-christmas", "lock-halloween" };

        public static IReadOnlyList<ChannelDef> All { get; } = Build();

        private static List<ChannelDef> Build()
        {
            var ws = ScheduleStrategies.WeightedShuffle;
            var mar = ScheduleStrategies.Marathon;
            var err = ScheduleStrategies.EpisodeRoundRobin;
            var list = new List<ChannelDef>
            {
                // ── The Marquee ──
                D("everything","Everything","The whole library on smart shuffle","The Marquee").In(MT),
                D("the-canon","The Canon","The all-time great films","The Marquee").Imdb(8.0),
                D("certified-fresh","Certified Fresh","Critics' darlings","The Marquee").Tomato(90),
                D("crowd-pleasers","Crowd-Pleasers","Films people love","The Marquee").Popcorn(80),
                D("hidden-gems","Hidden Gems","Great and under-seen","The Marquee").Imdb(7.5).Popular(null,8).Novelty(55),
                D("community-favorites","Community Favorites","Most-watched by our viewers","The Marquee").MinViewers(3),
                // Rolling, not a fixed .Year(2025,2026) range: ReleasedWithin re-reads "now" every time the
                // schedule is extended, so this channel ages itself and never needs a January edit.
                D("new-releases","New Releases","The last two years of movies","The Marquee").In(M).ReleasedWithin(2),

                // ── The Auteurs ──
                D("hitchcock","The Hitchcock Hour","The Master of Suspense","The Auteurs").Dir("Alfred Hitchcock"),
                D("herzog","Herzog: Ecstatic Truth","Fiction and fever-dream docs","The Auteurs").Dir("Werner Herzog"),
                D("kurosawa","Kurosawa","The Emperor of cinema","The Auteurs").Dir("Akira Kurosawa"),
                D("spielberg","Spielberg","The blockbuster maestro","The Auteurs").Dir("Steven Spielberg"),
                D("scorsese","Scorsese","Sinners and the city","The Auteurs").Dir("Martin Scorsese"),
                D("bergman","Bergman","Faith, death, and silence","The Auteurs").Dir("Ingmar Bergman"),
                D("cronenberg","Cronenberg: Body Horror","The new flesh","The Auteurs").Dir("David Cronenberg"),
                D("coens","The Coen Brothers","Crime and cosmic jokes","The Auteurs").Dir("Joel Coen","Ethan Coen"),
                D("wes-anderson","Wes Anderson","Symmetrical melancholy","The Auteurs").Dir("Wes Anderson"),
                D("lynch","David Lynch","Dreams and nightmares","The Auteurs").Dir("David Lynch"),
                D("burton","Tim Burton","Gothic whimsy","The Auteurs").Dir("Tim Burton"),
                D("kubrick","Kubrick","Cold, perfect, unsettling","The Auteurs").Dir("Stanley Kubrick"),
                D("carpenter","John Carpenter","The Master of Horror","The Auteurs").Dir("John Carpenter"),
                D("tarantino","Tarantino","Talk, then violence","The Auteurs").Dir("Quentin Tarantino"),
                D("woody-allen","Woody Allen","Neurotics, jazz & New York","The Auteurs").Dir("Woody Allen"),
                D("ford","John Ford","Monument Valley & myth","The Auteurs").Dir("John Ford"),
                D("wilder","Billy Wilder","Acid wit, noir & farce","The Auteurs").Dir("Billy Wilder"),
                D("hawks","Howard Hawks","Pros who do the job","The Auteurs").Dir("Howard Hawks"),
                D("huston","John Huston","Lost causes & treasure","The Auteurs").Dir("John Huston"),
                D("welles","Orson Welles","Deep focus, deeper shadows","The Auteurs").Dir("Orson Welles"),
                D("lumet","Sidney Lumet","The city's conscience","The Auteurs").Dir("Sidney Lumet"),
                D("coppola","Coppola","Family, power & opera","The Auteurs").Dir("Francis Ford Coppola"),
                D("eastwood","Clint Eastwood","Behind the squint","The Auteurs").Dir("Clint Eastwood"),
                D("ridley-scott","Ridley Scott","Worlds built to scale","The Auteurs").Dir("Ridley Scott"),
                D("nolan","Christopher Nolan","Time, mind & spectacle","The Auteurs").Dir("Christopher Nolan"),
                D("bunuel","Buñuel","Surreal & subversive","The Auteurs").Dir("Luis Buñuel"),
                D("godard","Godard","Breaking every rule","The Auteurs").Dir("Jean-Luc Godard"),
                D("fellini","Fellini","Carnival of the soul","The Auteurs").Dir("Federico Fellini"),
                D("ozu","Ozu","Stillness & family","The Auteurs").Dir("Yasujirō Ozu"),

                // ── Star Power ──
                D("cage","The Nicolas Cage Channel","Every Cage, all the time","Star Power").Star("Nicolas Cage"),
                D("slj","Samuel L. Jackson","SLJ headlines","Star Power").Star("Samuel L. Jackson"),
                D("arnold","Arnold","Schwarzenegger","Star Power").Star("Arnold Schwarzenegger"),
                D("deniro","De Niro","Robert De Niro","Star Power").Star("Robert De Niro"),
                D("hanks","The Tom Hanks Channel","America's dad","Star Power").Star("Tom Hanks"),
                D("murray","Bill Murray","Deadpan legend","Star Power").Star("Bill Murray"),
                D("depp","Johnny Depp","Eccentrics & pirates","Star Power").Star("Johnny Depp"),
                D("dafoe","Willem Dafoe","Intensity incarnate","Star Power").Star("Willem Dafoe"),
                D("willis","Bruce Willis","Yippee-ki-yay","Star Power").Star("Bruce Willis"),
                D("buscemi","Steve Buscemi","Character-actor royalty","Star Power").Star("Steve Buscemi"),
                D("robin-williams","Robin Williams","Manic genius","Star Power").Star("Robin Williams"),
                D("cruise","Tom Cruise","Does his own stunts","Star Power").Star("Tom Cruise"),
                D("nicholson","Jack Nicholson","Here's Johnny","Star Power").Star("Jack Nicholson"),
                D("connery","Sean Connery","The original Bond","Star Power").Star("Sean Connery"),
                D("caine","Michael Caine","Not many people know that","Star Power").Star("Michael Caine"),
                D("keanu","Keanu Reeves","Whoa","Star Power").Star("Keanu Reeves"),

                // ── Japanese Cinema & Samurai ──
                D("japanese-cinema","Japanese Cinema","From Ozu to Miike — Japanese films","Japanese Cinema & Samurai").Lang("ja"),
                D("zatoichi","Zatoichi: Blind Swordsman","The legendary samurai series","Japanese Cinema & Samurai").Star("Shintaro Katsu","Shintarô Katsu"),
                D("toho-kaiju","Godzilla & the Kaiju","Giant monsters stomp Tokyo","Japanese Cinema & Samurai").Franchise("godzilla"),

                // ── Horror ──
                D("horror","Horror","Frights all night","Horror").Genre("Horror"),
                D("stephen-king","Stephen King","Adaptations of the King","Horror").In(MT).Franchise("stephen king"),
                D("hammer-icons","Hammer Horror & Icons","Lee, Cushing & Vincent Price","Horror").Star("Christopher Lee","Peter Cushing","Vincent Price").Genre("Horror"),
                D("monster-movies","Classic Monster Movies","Black-and-white frights","Horror").Genre("Horror").Visual("black and white"),
                D("slasher-night","Slasher Night","Stalk, slash, sequel","Horror").Sub("slasher"),
                D("creature-features","Creature Features","Things with teeth","Horror").Sub("creature feature","kaiju"),
                D("gorehound","Gorehound","Not for the squeamish","Horror").Content("gore"),

                // ── Cult, Weird & Arthouse ──
                D("criterion","The Criterion Closet","Cinema as art","Cult, Weird & Arthouse").Path("Criterion"),
                D("arthouse","The Arthouse","The artistic canon","Cult, Weird & Arthouse").Sub("art house"),
                D("cult-vault","The Cult Vault","Beloved oddities","Cult, Weird & Arthouse").Cult(70).Imdb(null,8.3),
                D("schlock","Schlock Theater","Gloriously bad B-movies","Cult, Weird & Arthouse").Sub("b movie"),
                D("bw","In Glorious Black & White","Monochrome cinema","Cult, Weird & Arthouse").Visual("black and white"),
                D("surreal","Surreal Cinema","Dream logic","Cult, Weird & Arthouse").Surreal(70),
                D("neon-noir","Neon Noir","Synthwave sci-fi & neon nights","Cult, Weird & Arthouse").Genre("Sci-Fi","Action","Thriller").Visual("neon-soaked"),
                D("wordless","Wordless Wonders","Show, don't tell","Cult, Weird & Arthouse").Content("no dialogue"),
                // Judged (was Novelty>=70): an AI slider is not a curator. "Deep cut" is a claim about how
                // far off the beaten path a film sits, and the slider put Madoka Rebellion and The Matrix on
                // a channel of obscurities.
                D("off-beaten","Off the Beaten Path","Deep cuts and obscurities","Cult, Weird & Arthouse").In(M).Judged(),
                // Judged (was Runtime<=40 and nothing else): a runtime cap is not an identity. 45% of the old
                // pool was the Kid-Friendly Shorts set, so Winnie the Pooh and It's the Great Pumpkin aired
                // between Scorpio Rising and La Jetée. This is the grown-up shelf — arthouse, experimental,
                // silent and animation-as-art; the children's shorts belong to kid-shorts, the episodic
                // golden-age cartoons to cartoon-shorts.
                // AllowAdult for the same reason Adult Animation needs it: the avant-garde canon includes titles
                // the rating map classes as adult/banned (Un Chien Andalou, Meat Joy), and the age gate still
                // raises the channel's ceiling accordingly. Membership is judged, so nothing arrives by accident.
                D("short-films","Short Film Theater","Arthouse, experimental & early cinema in under 40 minutes","Cult, Weird & Arthouse").In(M).Judged().AllowAdult(),

                // ── Genres ──
                D("comedy","Comedy","Laughs around the clock","Genres").Genre("Comedy"),
                D("drama","Drama","Serious cinema","Genres").Genre("Drama"),
                D("action","Action & Adventure","Explosions and quests","Genres").Genre("Action","Adventure"),
                D("thrillers","Thrillers","Suspense and tension","Genres").Genre("Thriller"),
                D("scifi-fantasy","Sci-Fi & Fantasy","Other worlds","Genres").Genre("Sci-Fi","Fantasy"),
                // Absorbed Heist & Capers (88% of it was already Crime, and this description literally said
                // "capers"): one crime channel, spotlights reserved for genuinely distinct shelves.
                D("crime","Crime & Mob","Gangsters, capers and the criminal underworld","Genres").Genre("Crime"),
                // Absorbed Date Night (92% of it was already Romance): one romance channel, weepies included.
                D("romance","Romance","Love stories — swooning, sweeping and devastating","Genres").Genre("Romance"),
                D("documentaries","Documentaries","Real stories","Genres").Genre("Documentary"),
                // Judged (was Genre Musical + Music): those two genres overlap by only 33 titles, so 250
                // music-ADJACENT dramas — The Blue Angel, Almost Famous, Amadeus, Airheads — outnumbered the
                // musicals on a channel called Musicals. One channel still (Eric), but a judged one.
                D("musicals","Musicals & Music","Song, dance, and the films music built","Genres").In(M).Judged(),

                // ── Subgenre Spotlights ──
                D("film-noir","Film Noir","Shadows and moral fog","Subgenre Spotlights").Sub("noir","neo-noir"),
                D("spaghetti-western","Westerns","Saddle up — Leone to Eastwood","Subgenre Spotlights").Genre("Western"),
                D("psych-thriller","Psychological Thrillers","Mind games","Subgenre Spotlights").Sub("psychological thriller"),
                D("superhero","Superhero","Capes and cowls","Subgenre Spotlights").In(MT).Sub("superhero"),
                D("martial-arts","Enter the Dragon","Martial arts & kung fu","Subgenre Spotlights").Keyword("martial arts","martial-arts"),

                // ── Signature Picks ──
                D("time-travel","Time Travel","Past, future, repeat","Signature Picks").In(MT).Keyword("time travel","time-travel"),
                D("slow-burn","The Slow Burn","Patience rewarded","Signature Picks").Content("slow-burn"),
                D("tearjerkers","Bring Tissues","Tearjerkers","Signature Picks").Content("tearjerker"),
                D("spoofs","Spoofs & Mockumentaries","Brooks, Proft & the parody crew","Signature Picks").Sub("spoof","mockumentary"),
                D("mst3k","Mystery Science Theater","Riff over the worst movies ever made","Signature Picks").In(T).Path("MST3K","Mystery Science").Strat(err),
                D("coming-of-age","Coming of Age","Growing up on screen","Signature Picks").Tag(TagCategory.Theme,"coming of age"),

                // ── Decades ──
                D("silent-era","Silent & Pre-Code","Before 1930","Decades").Year(null,1929).Strat(ScheduleStrategies.SeededShuffle),
                D("golden-age","Golden Age Hollywood","1930s-1940s","Decades").Year(1930,1949),
                D("new-hollywood","New Hollywood","1970s","Decades").Year(1970,1979),
                D("eighties","Totally '80s","1980-1989","Decades").Year(1980,1989),
                D("nineties","The '90s","1990-1999","Decades").Year(1990,1999),
                D("twothousands","The 2000s","2000-2009","Decades").Year(2000,2009),
                D("eighties-horror","That '80s Horror","Slashers and synth screams","Decades").Year(1980,1989).Genre("Horror"),
                D("eighties-scifi","'80s Sci-Fi","Neon futures","Decades").Year(1980,1989).Genre("Sci-Fi"),

                // ── Moods & Vibes ──
                D("bleak","Bleak & Beautiful","Heavy and haunting","Moods & Vibes").Mood("melancholic","bleak"),
                D("dreamlike","Dreamlike","Hazy and strange","Moods & Vibes").Mood("dreamlike"),
                D("feel-good","Feel-Good & Wholesome","Leaves you smiling","Moods & Vibes").Mood("wholesome","uplifting"),
                D("whimsical","Whimsical & Quirky","Offbeat charm","Moods & Vibes").Mood("whimsical"),
                D("tense","Tense & Dreadful","White-knuckle dread","Moods & Vibes").Mood("tense","dread","unsettling"),
                D("adrenaline","Pure Adrenaline","Nonstop motion","Moods & Vibes").Energy(80),
                D("epics","Epics","Sweeping, big-canvas cinema","Moods & Vibes").Mood("epic"),

                // ── Animation Hall of Fame ──
                D("cartoon-shorts","Cartoon Shorts Theater","Looney Tunes, Tom & Jerry & the golden-age greats","Animation Hall of Fame").In(T).Judged().Strat(err),
                D("disney-classics","Disney Animated Classics","The Disney film canon","Animation Hall of Fame").Path("Disney Films","Walt Disney"),
                D("hanna-barbera","Hanna-Barbera Classics","Yabba dabba doo","Animation Hall of Fame").In(T).Judged().Strat(err),
                D("disney-afternoon","The Disney Afternoon","Classic Disney TV animation","Animation Hall of Fame").In(T).Judged().Strat(err),
                // New: episodic Disney had no home of its own, so Mickey Mouse and Chip 'n Dale were landing
                // on the shorts channels and burying the one-off films there (Eric). Disney's own television —
                // the Mickey shorts series, the Treasures collections, Pooh, Gravity Falls — lives here.
                D("disney-tv","The Disney Channel","Disney's own television, afternoon to bedtime","Animation Hall of Fame").In(T).Judged().Strat(err),
                D("animated-heroes","Animated Superheroes","Heroes in animation","Animation Hall of Fame").In(MT).Judged().Strat(err),
                D("stop-motion","Stop-Motion & Handcrafted","Made by hand","Animation Hall of Fame").In(MT).Visual("stop-motion","claymation","rotoscope"),
                // Judged pair (slate v3): [adult swim] is the literal Williams Street block; Adult
                // Animation is the art form beyond it (absorbed Animation for Grown-Ups). The old path
                // lists had the identities washed — Superjail/Frisky Dingo sat opposite their network.
                D("adult-swim","[adult swim]","The Williams Street block, faithfully","Animation Hall of Fame").In(T).Judged().Strat(err),
                // AllowAdult: the canon includes X/NC-17 landmarks (Fritz the Cat, Belladonna) that the
                // default adult exclusion would silently drop; membership is judged, so nothing arrives
                // by accident.
                D("adult-animation","Adult Animation","Animation as an adult art form — Bakshi to Akira","Animation Hall of Fame").In(MT).Judged().AllowAdult().Strat(err),

                // ── Kids & Family ──
                D("nickelodeon","Nickelodeon","'90s-2000s Nicktoons & live-action","Kids & Family").In(T).Judged().Strat(err),
                D("preschool","Preschool Corner","Gentlest TV for the littlest","Kids & Family").In(T).Mpaa(2).Judged().Strat(err),
                // Judged (was Genre Animation + Mpaa<=2, i.e. 100 series / 7,528 episodes — the entire
                // animated-TV library). Mechanical membership also put it OUTSIDE the toddler hard rule,
                // which canon can only enforce on judged channels: Blue's Clues, Peppa and Sesame Street
                // aired here alongside Duckman, Daria, Ren & Stimpy, Off the Air and most of the anime shelf.
                // The identity is the broadcast block itself — network/syndicated Saturday-morning TV.
                D("saturday-cartoons","Saturday Morning Cartoons","The network Saturday block — cereal optional","Kids & Family").In(T).Mpaa(2).Judged().Strat(err),
                // ── The judged family cluster (station briefs: docs/channel-slate-2026-07.md) ──
                // Membership is a per-title judgment loaded by load-channel-tags — NOT a facet formula.
                // The whole 2026-07 lesson: no facet separates "Jurassic Park, yes" from "The Dirty
                // Dozen, no", the AI Occasion tag is not a curation signal (24% coverage; Gremlins was
                // tagged both christmas AND halloween), and rating caps here are belt-and-braces only.
                // Regression canon: docs/channel-canon.json (channel-canon command).
                D("kid-films","Kid-Friendly Films","Gentle movies for the youngest viewers","Kids & Family").In(M).Mpaa(2).Judged(),
                D("family-night","Family Movie Night","Toy Story to Jurassic Park — the whole family","Kids & Family").In(M).Mpaa(3).Judged(),
                D("family-tv","The Family Room","Kid-safe shows to leave on all afternoon","Kids & Family").In(T).Mpaa(2).Judged().Strat(err),
                D("big-kid-horror","The Monster Club","Scary the way a sleepover is scary","Kids & Family").In(M).Mpaa(3).Judged(),
                D("read-learn","Read & Learn","Educational classics","Kids & Family").In(T).Mpaa(2).Judged().Strat(err),
                D("kid-shorts","Kid-Friendly Shorts","Real short films for young viewers — Pixar to the golden age","Kids & Family").In(M).Mpaa(2).Judged(),

                // ── Anime ──
                D("anime-central","Anime Central","All anime, all day","Anime").In(MT).Sub("anime").Strat(err),
                D("ghibli","Studio Ghibli","The Ghibli canon","Anime").Path("Studio Ghibli","Ghibli").Strat(mar),
                D("pokemon","Pokemon","Gotta catch 'em all","Anime").In(MT).Path("Pokémon","Pokemon").Strat(err),
                D("dragon-ball","Dragon Ball","Saiyan saga","Anime").In(MT).Path("Dragon Ball").Strat(mar),
                D("anime-arthouse","Anime Arthouse","The bold, strange & sublime","Anime").In(MT).Judged().Strat(err),
                D("modern-anime","Modern Anime","Today's hits","Anime").In(MT).Judged().Strat(err),
                D("classic-anime","Classic Anime","The canon","Anime").In(MT).Judged().Strat(err),
                // Judged (was Lang ja + Genre Animation): the genre rows carry Animation for any film with an
                // animated sequence, so Godzilla vs. the Smog Monster qualified as an anime film.
                D("anime-films","Anime Films","Feature-length anime","Anime").In(M).Judged(),

                // ── The TV Vault ──
                D("cult-tv","Cult TV","Riff-worthy and strange","The TV Vault").In(T).Judged().Strat(err),
                D("classic-scifi-tv","Classic Sci-Fi TV","Final frontiers","The TV Vault").In(T).Judged().Strat(err),
                D("prestige-drama","Prestige Drama","Appointment television","The TV Vault").In(T).Judged().Strat(err),
                D("classic-sitcoms","Classic Sitcoms","Comfort comedy","The TV Vault").In(T).Judged().Strat(err),
                D("science-tv","Science & Nature","How the world works","The TV Vault").In(T).Judged().Strat(err),
                D("muppets","The Muppets & Jim Henson","Felt and felt deeply","The TV Vault").In(MT).Judged().Strat(err),
                D("primetime-toons","Primetime Animation","The Simpsons, Futurama & adult-cartoon primetime","The TV Vault").In(T).Judged().Strat(err),
                D("sketch-comedy","Sketch Comedy","Monty Python to the Whitest Kids","The TV Vault").In(T).Judged().Strat(err),
                D("web-series","Web Series","Internet originals & machinima","The TV Vault").In(T).Judged().Strat(err),
                // New; absorbed Stunts & Pranks whole (Eric). The MTV-shaped gap in the lineup: the 11-16
                // shelf had nowhere to go, so Daria and Duckman washed up on Saturday Morning Cartoons and
                // Beavis on Primetime Animation. Dares, alt-cartoons and noise — old enough to be rude, young
                // enough that it isn't [adult swim].
                D("channel-zero","Channel Zero","Too old for Nick, too young for [adult swim]","The TV Vault").In(MT).Judged().Strat(err),
                D("arthouse-tv","Arthouse TV","Long-form cinema — Decalogue to Berlin Alexanderplatz","The TV Vault").In(T).Judged().Strat(err),

                // ── International ──
                D("world-cinema","World Cinema","Beyond Hollywood","International").NotLang("en"),
                D("french-cinema","French Cinema","La Nouvelle Vague & beyond","International").Lang("fr"),
                D("german-cinema","German Cinema","Expressionism to New German","International").Lang("de"),
                D("italian-cinema","Italian Cinema","Neorealism to giallo","International").Lang("it"),
                D("spanish-cinema","Spanish-Language","Films in Spanish","International").Lang("es"),
                D("chinese-cinema","Chinese-Language Cinema","Mainland, Hong Kong & Taiwan","International").Lang("zh","cn"),
                D("korean-cinema","Korean Cinema","Melodrama to mayhem","International").Lang("ko"),
                D("russian-cinema","Russian & Soviet Cinema","Tarkovsky to the Thaw","International").Lang("ru"),
                D("scandi-cinema","Scandinavian Cinema","The bleak, beautiful North","International").Lang("sv","da","no"),

                // ── The Networks (judged station identities — slate v4) ──
                D("superstation","The Superstation","The movie you stop on while flipping","The Networks").In(M).Judged(),
                D("after-dark","After Dark","The kids are asleep — R-rated comfort","The Networks").In(M).Judged(),
                D("public-access","Public Access","1 a.m. UHF — found footage & outsider video","The Networks").In(ContentKinds.Movies | ContentKinds.Misc).Judged(),
                D("whodunit","Whodunit Weekend","A body in the library, never in the walls","The Networks").In(M).Judged(),
                D("lazy-sunday","Lazy Sunday","Nap-adjacent epics for the long afternoon","The Networks").In(M).Judged(),
                // Date Night retired into Romance (Eric): 92% of its 285 titles were already Romance-genre,
                // so it read as a duplicate rail rather than a station. Its judged tags stay on the titles.

                // ── Seasonal (date-windowed; the 2x2: family x adult, October x December) ──
                D("spooky-season","Spooky Season","The Halloween mood — spooky, not just gory","Seasonal").In(MT).Judged().Season(10,1,11,1),
                D("witching-hour","The Witching Hour","October, after the kids are asleep","Seasonal").In(M).Judged().Season(10,1,11,1),
                D("holiday-cheer","Holiday Cheer","Actually-Christmas movies — the Rudolf rule","Seasonal").In(MT).Judged().Season(12,1,1,2),
                D("naughty-list","The Naughty List","Christmas after bedtime — tinsel & whiskey","Seasonal").In(M).Judged().Season(12,1,1,2),
            };
            return list;
        }
    }
}
