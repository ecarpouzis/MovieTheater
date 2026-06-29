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
                D("so-bad-good","So Bad It's Good","The disasterpieces","Cult, Weird & Arthouse").Imdb(null,5.0).Cult(45),
                D("bw","In Glorious Black & White","Monochrome cinema","Cult, Weird & Arthouse").Visual("black and white"),
                D("surreal","Surreal Cinema","Dream logic","Cult, Weird & Arthouse").Surreal(70),
                D("neon-noir","Neon Noir","Synthwave sci-fi & neon nights","Cult, Weird & Arthouse").Genre("Sci-Fi","Action","Thriller").Visual("neon-soaked"),
                D("wordless","Wordless Wonders","Show, don't tell","Cult, Weird & Arthouse").Content("no dialogue"),
                D("off-beaten","Off the Beaten Path","Deep cuts and obscurities","Cult, Weird & Arthouse").Novelty(70),
                D("short-films","Short Film Theater","Pixar to Lynch — animation, experiment & early cinema","Cult, Weird & Arthouse").Runtime(null,40),

                // ── Genres ──
                D("comedy","Comedy","Laughs around the clock","Genres").Genre("Comedy"),
                D("drama","Drama","Serious cinema","Genres").Genre("Drama"),
                D("action","Action & Adventure","Explosions and quests","Genres").Genre("Action","Adventure"),
                D("thrillers","Thrillers","Suspense and tension","Genres").Genre("Thriller"),
                D("scifi-fantasy","Sci-Fi & Fantasy","Other worlds","Genres").Genre("Sci-Fi","Fantasy"),
                D("crime","Crime & Mob","Gangsters and capers","Genres").Genre("Crime"),
                D("romance","Romance","Love stories","Genres").Genre("Romance"),
                D("documentaries","Documentaries","Real stories","Genres").Genre("Documentary"),
                D("musicals","Musicals & Music","Song, dance, and bands","Genres").Genre("Musical","Music"),

                // ── Subgenre Spotlights ──
                D("film-noir","Film Noir","Shadows and moral fog","Subgenre Spotlights").Sub("noir","neo-noir"),
                D("spaghetti-western","Westerns","Saddle up — Leone to Eastwood","Subgenre Spotlights").Genre("Western"),
                D("psych-thriller","Psychological Thrillers","Mind games","Subgenre Spotlights").Sub("psychological thriller"),
                D("heist","Heist & Capers","Plans and double-crosses","Subgenre Spotlights").Sub("heist"),
                D("superhero","Superhero","Capes and cowls","Subgenre Spotlights").In(MT).Sub("superhero"),
                D("martial-arts","Enter the Dragon","Martial arts & kung fu","Subgenre Spotlights").Keyword("martial arts","martial-arts"),

                // ── Signature Picks ──
                D("time-travel","Time Travel","Past, future, repeat","Signature Picks").In(MT).Keyword("time travel","time-travel"),
                D("slow-burn","The Slow Burn","Patience rewarded","Signature Picks").Content("slow-burn"),
                D("tearjerkers","Bring Tissues","Tearjerkers","Signature Picks").Content("tearjerker"),
                D("spoofs","Spoofs & Mockumentaries","Brooks, Proft & the parody crew","Signature Picks").Sub("spoof","mockumentary"),
                D("mst3k","Mystery Science Theater","Riff over the worst movies ever made","Signature Picks").In(T).Path("MST3K","Mystery Science").Strat(err),
                D("silent-comedy","Silent Comedy","Keaton, Chaplin & the pioneers","Signature Picks").Star("Buster Keaton","Charles Chaplin").Year(null,1936),
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
                D("cartoon-shorts","Cartoon Shorts Theater","Looney Tunes, Tom & Jerry & the golden-age greats","Animation Hall of Fame").In(T).Path("Looney Tunes","Merrie Melodies","Tom and Jerry","Walt Disney Treasures","Walt Disney Fables","Tex Avery").Strat(err),
                D("disney-classics","Disney Animated Classics","The Disney film canon","Animation Hall of Fame").Path("Disney Films","Walt Disney"),
                D("hanna-barbera","Hanna-Barbera Classics","Yabba dabba doo","Animation Hall of Fame").In(T).Path("Flintstones","Jetsons","Scooby Doo","Wacky Races","Atom Ant","Rocky and Bullwinkle").Strat(err),
                D("disney-afternoon","The Disney Afternoon","Classic Disney TV animation","Animation Hall of Fame").In(T).Path("DuckTales","TaleSpin","Darkwing Duck","Chip 'n' Dale","Gargoyles").Strat(err),
                D("animated-heroes","Animated Superheroes","Heroes in animation","Animation Hall of Fame").In(MT).Path("Marvel Animated","Batman The Animated","X-Men","Spider-Man","Avatar - The Last Airbender","Korra").Strat(err),
                D("stop-motion","Stop-Motion & Handcrafted","Made by hand","Animation Hall of Fame").In(MT).Visual("stop-motion","claymation","rotoscope"),
                D("adult-swim","[adult swim]","Late-night adult animation","Animation Hall of Fame").In(T).Path("Aqua Teen","Sealab","Metalocalypse","Venture Bros","Samurai Jack","Rick and Morty","Harley Quinn","Off the Air","Space Dandy").Strat(err),
                D("animation-grownups","Animation for Grown-Ups","Not for kids","Animation Hall of Fame").In(MT).Genre("Animation").Intensity(60,null),

                // ── Kids & Family ──
                D("nickelodeon","Nickelodeon","'90s-2000s Nicktoons & live-action","Kids & Family").In(T).Path("SpongeBob","Ren & Stimpy","Rugrats","Wild Thornberrys","CatDog","Angry Beavers","Aaahh","Invader Zim","KaBlam","All That","Are You Afraid of the Dark","Pete & Pete","Blues Clues","Salute Your Shorts","Legends of the Hidden Temple").Strat(err),
                D("preschool","Preschool Corner","Gentlest TV for the littlest","Kids & Family").In(T).Path("Mister Rogers","Sesame Street","Bluey","Peppa Pig","Blues Clues").Strat(err),
                D("saturday-cartoons","Saturday Morning Cartoons","Classic toon energy","Kids & Family").In(MT).Genre("Animation").Mpaa(2).Strat(err),
                D("family-night","Family Movie Night","Something for everyone","Kids & Family").In(MT).Mpaa(3).Occasion("family-night"),
                D("read-learn","Read & Learn","Educational classics","Kids & Family").In(T).Path("Reading Rainbow","Magic School Bus","Schoolhouse Rock","Ada Twist","Hilda").Strat(err),
                D("sing-along","Sing-Along Musicals","Songs for all ages","Kids & Family").In(MT).Genre("Musical").Mpaa(2),
                D("kid-shorts","Kid-Friendly Shorts","Gentle short cartoons for the littlest","Kids & Family").In(T).Path("Schoolhouse Rock","Wonderful World of Mickey","Mickey Mouse (2013","Bluey Minisode","Cracking Contraption").Strat(err),

                // ── Anime ──
                D("anime-central","Anime Central","All anime, all day","Anime").In(MT).Sub("anime").Strat(err),
                D("ghibli","Studio Ghibli","The Ghibli canon","Anime").Path("Studio Ghibli","Ghibli").Strat(mar),
                D("pokemon","Pokemon","Gotta catch 'em all","Anime").In(MT).Path("Pokémon","Pokemon").Strat(err),
                D("dragon-ball","Dragon Ball","Saiyan saga","Anime").In(MT).Path("Dragon Ball").Strat(mar),
                D("anime-arthouse","Anime Arthouse","The bold, strange & sublime","Anime").In(MT).Path("Odd Taxi","Redline","Paprika","Perfect Blue","Paranoia Agent","Tatami","Experiments Lain","Cat Soup","Belladonna","Angel's Egg","Mind Game","Tekkon","Millennium Actress","Tokyo Godfathers","Ping Pong","Mononoke","Kaiba","Dead Leaves","Texhnolyze","Space Dandy").Strat(err),
                D("modern-anime","Modern Anime","Today's hits","Anime").In(MT).Path("Demon Slayer","Jujutsu Kaisen","Attack on Titan","One Punch Man","Re - Zero","Steins","Chainsaw Man").Strat(err),
                D("classic-anime","Classic Anime","The canon","Anime").In(MT).Path("Ranma","Cowboy Bebop","Trigun","Evangelion","Samurai Champloo","Ghost in the Shell","Fullmetal Alchemist",@"\Monster (2004","Death Note","Haruhi","Planetes").Strat(err),
                D("anime-films","Anime Films","Feature-length anime","Anime").Lang("ja").Genre("Animation"),

                // ── The TV Vault ──
                D("cult-tv","Cult TV","Riff-worthy and strange","The TV Vault").In(T).Path("Twilight Zone","Doctor Who","Lexx","Are You Afraid of the Dark","Tales from","Prisoner","Eerie","Hitchhiker","Neverwhere").Strat(err),
                D("classic-scifi-tv","Classic Sci-Fi TV","Final frontiers","The TV Vault").In(T).Path("Star Trek","Battlestar Galactica","Farscape","Buck Rogers","Quantum Leap").Strat(err),
                D("prestige-drama","Prestige Drama","Appointment television","The TV Vault").In(T).Path("Game of Thrones","Breaking Bad","Lost (2004","Twin Peaks","The Boys","Firefly","Westworld","Stranger Things","Band of Brothers","Sandman","Good Omens","Fringe","Roots","Pluribus","Alien - Earth","I'm a Virgo","Twisted Metal","Blue Eye Samurai").Strat(err),
                D("classic-sitcoms","Classic Sitcoms","Comfort comedy","The TV Vault").In(T).Path("Seinfeld","All in the Family","Community","Arrested Development","Archer","Spaced","Flight of the Conchords","Police Squad","Look Around You").Strat(err),
                D("science-tv","Science & Nature","How the world works","The TV Vault").In(T).Path("How It's Made","MythBusters","Penn & Teller","Cosmos","Planet Earth","deGrasse","M Theory","Fun to Imagine","Bill Nye","Book of Cool").Strat(err),
                D("muppets","The Muppets & Jim Henson","Felt and felt deeply","The TV Vault").In(MT).Path("Jim Henson","Muppet").Strat(err),
                D("primetime-toons","Primetime Animation","The Simpsons, Futurama & adult-cartoon primetime","The TV Vault").In(T).Path("Simpsons","Futurama","Daria","Beavis and Butt-Head","Duckman","Critic","Clone High").Strat(err),
                D("sketch-comedy","Sketch Comedy","Monty Python to the Whitest Kids","The TV Vault").In(T).Path("Monty Python","Whitest Kids","Liquid Television","Banzai","Mr. Show","Kids in the Hall","Hey, Vern").Strat(err),
                D("web-series","Web Series","Internet originals & machinima","The TV Vault").In(T).Path("Red Versus Blue","Marble Hornets","Auralnaut").Strat(err),
                D("adult-animation","Adult Animation","Surreal, transgressive toons","The TV Vault").In(T).Path("Aeon Flux","Æon Flux","Maxx","Superjail","Spicy City","Midnight Gospel","Death Parade","Dicktown","Frisky Dingo","Wonder Showzen","Love, Death").Strat(err),
                D("stunts-pranks","Stunts & Pranks","Dares, mayhem & mind games","The TV Vault").In(T).Path("Jackass","Banzai","Trick of the Mind").Strat(err),
                D("arthouse-tv","Arthouse TV","Long-form cinema — Decalogue to Berlin Alexanderplatz","The TV Vault").In(T).Path("Berlin Alexanderplatz","Heimat","Decalogue","Phantom India","Histoire","Tanner").Strat(err),

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

                // ── Seasonal (date-windowed) ──
                D("spooky-season","Spooky Season","The Halloween mood — spooky, not just gory","Seasonal").Occasion("halloween").Season(10,1,11,1),
                D("holiday-cheer","Holiday Cheer","Christmas & winter holidays","Seasonal").Occasion("christmas","holiday").Season(12,1,1,2),
                D("summer-blockbusters","Summer Blockbusters","Big summer fun","Seasonal").Genre("Action","Adventure").Energy(70).Season(5,25,9,5),
                D("sweethearts","Sweetheart Cinema","Valentine's rom-coms","Seasonal").GenreAll("Romance","Comedy").Season(2,1,2,15),
            };
            return list;
        }
    }
}
