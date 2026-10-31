using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using LymdunetteBot.Models;

namespace LymdunetteBot.Services;

/// <summary>
/// Finds the Spotify release of the track currently played on r-a-d.io, and only that release:
/// the artist must be the same, the version (remix, live, TV size...) must be the same, and the
/// duration must agree when the radio knows it. Romaji and kana titles/artists are compared after
/// converting kana to romaji; kanji titles are matched through the artist + duration instead.
/// </summary>
internal static class SpotifyTrackMatcher {
    const double ARTIST_SIMILARITY = 0.85;
    const double TITLE_SIMILARITY = 0.8;
    const double REMIXER_SIMILARITY = 0.8;
    static readonly TimeSpan DurationTolerance = TimeSpan.FromSeconds(5);
    static readonly TimeSpan CrossScriptDurationTolerance = TimeSpan.FromSeconds(3);
    static readonly TimeSpan MinPlausibleDuration = TimeSpan.FromSeconds(30);
    static readonly TimeSpan MaxPlausibleDuration = TimeSpan.FromMinutes(20);
    const int ARTIST_CATALOG_PAGES = 5;
    const int MAX_RESOLVED_ARTISTS = 2;

    internal sealed record TrackTitle(
        string Compact,
        IReadOnlyList<string> Variants,
        IReadOnlySet<string> Tags,
        string Remixer,
        IReadOnlyList<string> FeaturedArtists);

    internal sealed record RadioTrack(
        string ArtistText,
        IReadOnlyList<string> Artists,
        string TitleText,
        string SearchTitle,
        TrackTitle Title,
        TimeSpan? Duration);

    public static async Task<SpotifyTrack?> FindAsync(ISpotifySearch spotify, string nowPlaying, TimeSpan? duration,
        CancellationToken cancellationToken = default) {
        RadioTrack? radio = ParseNowPlaying(nowPlaying, duration);
        if (radio == null)
            return null; // without an artist we cannot guarantee the right singer

        var noTrustedArtists = new HashSet<string>();
        string primaryArtist = StripQuotes(radio.Artists[0]);

        // 1. Free-text search, as typed on the radio (keeps remix/version words).
        SpotifyTrack? match = Select(radio,
            await spotify.SearchTracksAsync($"{radio.ArtistText} {radio.TitleText}", 0, cancellationToken), noTrustedArtists);
        if (match != null) return match;

        // 2. Field-restricted search.
        match = Select(radio, await spotify.SearchTracksAsync(
            $"artist:\"{primaryArtist}\" track:\"{StripQuotes(radio.SearchTitle)}\"", 0, cancellationToken), noTrustedArtists);
        if (match != null) return match;

        // 3. Resolve the artist on Spotify (e.g. "Kenshi Yonezu" -> "米津玄師") and search with its real name.
        IReadOnlyList<SpotifyArtist> artists = await ResolveArtistsAsync(spotify, radio, cancellationToken);
        if (artists.Count == 0)
            return null;

        var trustedArtistIds = artists.Select(a => a.Id!).ToHashSet();
        foreach (SpotifyArtist artist in artists) {
            match = Select(radio, await spotify.SearchTracksAsync(
                $"artist:\"{StripQuotes(artist.Name!)}\" {radio.TitleText}", 0, cancellationToken), trustedArtistIds);
            if (match != null) return match;
        }

        // 4. Title written in another script (romaji vs kanji): browse the artist's tracks and rely on the duration.
        if (!IsPlausibleDuration(radio.Duration))
            return null;

        var catalog = new List<SpotifyTrack>();
        for (int page = 0; page < ARTIST_CATALOG_PAGES; page++) {
            IReadOnlyList<SpotifyTrack> tracks = await spotify.SearchTracksAsync(
                $"artist:\"{StripQuotes(artists[0].Name!)}\"", page * SpotifyClient.SEARCH_PAGE_SIZE, cancellationToken);
            catalog.AddRange(tracks);
            if (tracks.Count < SpotifyClient.SEARCH_PAGE_SIZE)
                break;
        }

        return Select(radio, catalog, trustedArtistIds);
    }

    /// <summary>Picks the only acceptable candidate, or null when nothing is reliable enough.</summary>
    internal static SpotifyTrack? Select(RadioTrack radio, IEnumerable<SpotifyTrack> candidates, IReadOnlySet<string> trustedArtistIds) {
        var gated = candidates
            .Where(c => !string.IsNullOrEmpty(c.ExternalUrls?.Spotify) && !string.IsNullOrEmpty(c.Name))
            .Select(c => (Track: c, Title: ParseTitle(c.Name!)))
            .Where(c => ArtistMatches(radio, c.Track, c.Title, trustedArtistIds)
                        && VersionMatches(radio.Title, c.Title)
                        && DurationMatches(radio.Duration, c.Track, DurationTolerance))
            .ToList();

        var best = gated
            .Select(c => (c.Track, Score: TitleSimilarity(radio.Title, c.Title)))
            .Where(c => c.Score >= TITLE_SIMILARITY)
            .OrderByDescending(c => c.Score)
            .ThenBy(c => DurationDelta(radio.Duration, c.Track))
            .FirstOrDefault();
        if (best.Track != null)
            return best.Track;

        // Titles that cannot be compared (one side romaji, the other kanji): accept only when the
        // duration designates a single song of that artist. The same song can exist on several
        // releases (single, album, best-of), so count distinct titles rather than tracks.
        if (!IsPlausibleDuration(radio.Duration) || radio.Title.Compact.Length == 0)
            return null;

        var crossScript = gated
            .Where(c => IsNonLatin(radio.Title.Compact) != IsNonLatin(c.Title.Compact)
                        && DurationMatches(radio.Duration, c.Track, CrossScriptDurationTolerance))
            .ToList();

        return crossScript.Select(c => c.Title.Compact).Distinct().Count() == 1
            ? crossScript[0].Track
            : null;
    }

    static async Task<IReadOnlyList<SpotifyArtist>> ResolveArtistsAsync(ISpotifySearch spotify, RadioTrack radio,
        CancellationToken cancellationToken) {
        var resolved = new List<SpotifyArtist>();

        foreach (string name in radio.Artists.Take(MAX_RESOLVED_ARTISTS)) {
            IReadOnlyList<SpotifyArtist> results = (await spotify.SearchArtistsAsync(name, cancellationToken))
                .Where(a => !string.IsNullOrEmpty(a.Id) && !string.IsNullOrEmpty(a.Name))
                .ToList();
            if (results.Count == 0)
                continue;

            string compactName = Compact(name);
            var similar = results.Where(a => Similarity(compactName, Compact(a.Name!)) >= ARTIST_SIMILARITY).ToList();

            if (similar.Count > 0) {
                resolved.AddRange(similar);
            } else if (IsNonLatin(compactName) != IsNonLatin(Compact(results[0].Name!))) {
                // Romaji name on the radio, native name on Spotify (or the reverse): Spotify knows the
                // aliases, so trust its best result. Title/version/duration checks still apply.
                resolved.Add(results[0]);
            }
        }

        return resolved.DistinctBy(a => a.Id).ToList();
    }

    #region Parsing

    static readonly Regex ArtistSeparator = new(
        @"\s*(?:,|、|，|&|＆|×|\s+x\s+|\s+and\s+|\bfeat\.?\s|\bft\.?\s|\bfeaturing\s|\bvs\.?\s|\bwith\s|\+)\s*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly Regex Parenthesized = new(@"[\(（\[【](?<content>[^\)）\]】]*)[\)）\]】]", RegexOptions.CultureInvariant);
    static readonly Regex DashSuffix = new(@"\s+[-–—]\s+(?<content>[^-–—]+)$", RegexOptions.CultureInvariant);
    static readonly Regex VoiceActorPrefix = new(@"^\s*(?:cv|vo|voice)\s*[\.:：]\s*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static RadioTrack? ParseNowPlaying(string? nowPlaying, TimeSpan? duration) {
        if (string.IsNullOrWhiteSpace(nowPlaying))
            return null;

        string text = nowPlaying.Trim();
        int separator = text.IndexOf(" - ", StringComparison.Ordinal);
        if (separator <= 0)
            return null;

        string artistText = text[..separator].Trim();
        string titleText = text[(separator + 3)..].Trim();
        if (artistText.Length == 0 || titleText.Length == 0)
            return null;

        TrackTitle title = ParseTitle(titleText);
        var artists = SplitArtists(artistText).Concat(title.FeaturedArtists)
            .Where(a => Compact(a).Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (artists.Count == 0)
            return null;

        string searchTitle = Parenthesized.Replace(titleText, " ");
        searchTitle = DashSuffix.Match(searchTitle) is { Success: true } dash
                      && ClassifySegment(dash.Groups["content"].Value, out _, out _) != SegmentKind.Unknown
            ? searchTitle[..dash.Index]
            : searchTitle;

        return new RadioTrack(artistText, artists, titleText, CollapseSpaces(searchTitle), title, duration);
    }

    /// <summary>Splits "A feat. B & C (CV: D)" into every name worth comparing, primary artist first.</summary>
    internal static List<string> SplitArtists(string artistText) {
        var names = new List<string>();

        foreach (string part in ArtistSeparator.Split(artistText)) {
            string withoutParens = Parenthesized.Replace(part, " ").Trim();
            if (withoutParens.Length > 0)
                names.Add(withoutParens);

            foreach (Match m in Parenthesized.Matches(part)) {
                string inner = VoiceActorPrefix.Replace(m.Groups["content"].Value, string.Empty).Trim();
                if (inner.Length > 0)
                    names.Add(inner);
            }
        }

        names.Add(artistText.Trim()); // the whole credit can also be a single name ("Simon & Garfunkel")
        return names;
    }

    internal static TrackTitle ParseTitle(string rawTitle) {
        var tags = new HashSet<string>(StringComparer.Ordinal);
        var featured = new List<string>();
        var unknownSegments = new List<string>();
        string remixer = string.Empty;

        void HandleSegment(string segment) {
            switch (ClassifySegment(segment, out string tag, out string detail)) {
                case SegmentKind.Version:
                    tags.Add(tag);
                    if (tag == "remix") remixer = detail;
                    break;
                case SegmentKind.Featuring:
                    featured.AddRange(SplitArtists(detail));
                    break;
                case SegmentKind.Unknown:
                    unknownSegments.Add(segment);
                    break;
            }
        }

        string title = rawTitle;
        foreach (Match m in Parenthesized.Matches(title))
            HandleSegment(m.Groups["content"].Value);
        title = Parenthesized.Replace(title, " ");

        // Spotify style suffixes: "Title - Remastered 2011", "Title - Live", "Title - TV Size".
        Match dash = DashSuffix.Match(title);
        while (dash.Success && ClassifySegment(dash.Groups["content"].Value, out _, out _) != SegmentKind.Unknown) {
            HandleSegment(dash.Groups["content"].Value);
            title = title[..dash.Index];
            dash = DashSuffix.Match(title);
        }

        string compact = Compact(title);
        var variants = new List<string> { compact };
        foreach (string segment in unknownSegments) {
            // e.g. Spotify "怪物 (Kaibutsu)" or a subtitle the radio omits / includes.
            variants.Add(Compact(segment));
            variants.Add(compact + Compact(segment));
        }

        return new TrackTitle(compact, variants.Where(v => v.Length > 0).Distinct().ToList(), tags, remixer, featured);
    }

    enum SegmentKind { Unknown, Ignored, Featuring, Version }

    static readonly (Regex Pattern, SegmentKind Kind, string Tag)[] SegmentRules = [
        (Rx(@"^(?:feat\.?|ft\.?|featuring|with)\s+(?<detail>.+)$"), SegmentKind.Featuring, ""),
        (Rx(@"remaster|リマスター"), SegmentKind.Ignored, ""),
        (Rx(@"\b(?:original|album|single)\s+(?:mix|ver\.?|version)\b|^(?:mono|stereo)(?:\s+ver\.?|\s+version)?$"), SegmentKind.Ignored, ""),
        (Rx(@"^from\b"), SegmentKind.Ignored, ""),
        (Rx(@"\b(?:tv|anime|movie|game)\s*(?:size|ver\.?|version|edit|サイズ)|tvサイズ|ゲームサイズ|\bshort\s*(?:ver\.?|version|size)?\b"), SegmentKind.Version, "short"),
        (Rx(@"\bextended\b"), SegmentKind.Version, "extended"),
        (Rx(@"\b(?:re)?mix(?:ed)?\b|\brmx\b|\bbootleg\b|\bflip\b|\brefix\b|\brework\b|\bvip\b|リミックス"), SegmentKind.Version, "remix"),
        (Rx(@"\bedit\b"), SegmentKind.Version, "edit"),
        (Rx(@"\blive\b|ライブ"), SegmentKind.Version, "live"),
        (Rx(@"\bacoustic\b|\bunplugged\b|アコースティック"), SegmentKind.Version, "acoustic"),
        (Rx(@"\binstrumental\b|\binst\b|off\s*vocal|\bkaraoke\b|backing track|カラオケ|オフボーカル|インスト"), SegmentKind.Version, "instrumental"),
        (Rx(@"\ba\s*cappella\b|\bacapella\b"), SegmentKind.Version, "acapella"),
        (Rx(@"\bcover\b|カバー"), SegmentKind.Version, "cover"),
        (Rx(@"\bnightcore\b"), SegmentKind.Version, "nightcore"),
        (Rx(@"\bsped\s*up\b|\bspeed\s*up\b|\bslowed\b|\breverb\b|\b8d\b"), SegmentKind.Version, "altered"),
        (Rx(@"\bdemo\b"), SegmentKind.Version, "demo"),
        (Rx(@"\bver\b\.?|\bversion\b|バージョン|ヴァージョン"), SegmentKind.Version, "ver"),
        // Tie-in notes ("TV Anime X Opening Theme") say nothing about the recording; checked last so
        // that e.g. "(Ed Sheeran Remix)" is still read as a remix.
        (Rx(@"\b(?:op|ed|opening|ending|insert song|theme|soundtrack|ost)\b|主題歌|挿入歌|オープニング|エンディング"), SegmentKind.Ignored, ""),
    ];

    static readonly Regex RemixWords = Rx(@"\b(?:re)?mix(?:ed)?\b|\brmx\b|\bbootleg\b|\bflip\b|\brefix\b|\brework\b|\bvip\b|リミックス");
    static readonly Regex VersionWords = Rx(@"\bver\b\.?|\bversion\b|バージョン|ヴァージョン");

    static Regex Rx(string pattern) => new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    static SegmentKind ClassifySegment(string segment, out string tag, out string detail) {
        string text = segment.Normalize(NormalizationForm.FormKC).Trim();
        foreach ((Regex pattern, SegmentKind kind, string ruleTag) in SegmentRules) {
            Match m = pattern.Match(text);
            if (!m.Success)
                continue;

            tag = ruleTag;
            detail = kind switch {
                SegmentKind.Featuring => m.Groups["detail"].Value,
                SegmentKind.Version when ruleTag == "remix" => Compact(RemixWords.Replace(text, " ")),
                _ => string.Empty
            };
            if (ruleTag == "ver")
                tag = "ver:" + Compact(VersionWords.Replace(text, " ")); // "English ver." -> "ver:english"
            return kind;
        }

        tag = detail = string.Empty;
        return SegmentKind.Unknown;
    }

    #endregion

    #region Gates

    static bool ArtistMatches(RadioTrack radio, SpotifyTrack track, TrackTitle trackTitle, IReadOnlySet<string> trustedArtistIds) {
        List<SpotifyArtist> trackArtists = track.Artists ?? [];
        if (trackArtists.Any(a => a.Id != null && trustedArtistIds.Contains(a.Id)))
            return true;

        var candidates = trackArtists
            .Where(a => !string.IsNullOrEmpty(a.Name))
            .Select(a => a.Name!)
            .Concat(trackTitle.FeaturedArtists)
            .Select(Compact)
            .ToList();
        return radio.Artists.Select(Compact).Any(r => candidates.Any(s => Similarity(r, s) >= ARTIST_SIMILARITY));
    }

    static bool VersionMatches(TrackTitle radio, TrackTitle spotify) {
        if (!radio.Tags.SetEquals(spotify.Tags))
            return false;

        // Same kind of remix is not enough: it has to be the same remixer.
        if (radio.Tags.Contains("remix") && radio.Remixer.Length > 0 && spotify.Remixer.Length > 0)
            return Similarity(radio.Remixer, spotify.Remixer) >= REMIXER_SIMILARITY
                   || ContainsMeaningfully(radio.Remixer, spotify.Remixer);

        return true;
    }

    static bool IsPlausibleDuration(TimeSpan? duration)
        => duration is { } d && d >= MinPlausibleDuration && d <= MaxPlausibleDuration;

    static bool DurationMatches(TimeSpan? radioDuration, SpotifyTrack track, TimeSpan tolerance)
        => !IsPlausibleDuration(radioDuration) || DurationDelta(radioDuration, track) <= tolerance;

    static TimeSpan DurationDelta(TimeSpan? radioDuration, SpotifyTrack track)
        => IsPlausibleDuration(radioDuration)
            ? (radioDuration!.Value - TimeSpan.FromMilliseconds(track.DurationMs)).Duration()
            : TimeSpan.Zero;

    static double TitleSimilarity(TrackTitle radio, TrackTitle spotify) {
        double best = 0;
        foreach (string a in radio.Variants)
        foreach (string b in spotify.Variants) {
            double score = ContainsMeaningfully(a, b) ? 0.9 : Similarity(a, b);
            best = Math.Max(best, score);
        }
        return best;
    }

    #endregion

    #region Normalization

    /// <summary>
    /// Lowercase, width-folded, accent-free, kana converted to romaji, without spaces or punctuation,
    /// with long vowels folded so that "Toukyou", "Tōkyō", "Tokyo" and "とうきょう" are equal.
    /// </summary>
    internal static string Compact(string text) {
        string s = text.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        s = KanaToRomaji(s);

        var sb = new StringBuilder(s.Length);
        foreach (char c in s.Normalize(NormalizationForm.FormD)) {
            UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(c);
            if (category == UnicodeCategory.NonSpacingMark)
                continue;
            if (char.IsLetterOrDigit(c))
                sb.Append(c);
        }

        return FoldRomaji(sb.ToString().Normalize(NormalizationForm.FormC));
    }

    static readonly (string From, string To)[] RomajiFolds = [
        ("ou", "o"), ("oo", "o"), ("uu", "u"), ("aa", "a"), ("ii", "i"), ("ee", "e"),
        ("wo", "o"), ("mb", "nb"), ("mp", "np"),
    ];

    static string FoldRomaji(string s) {
        foreach ((string from, string to) in RomajiFolds)
            s = s.Replace(from, to, StringComparison.Ordinal);
        return s;
    }

    internal static bool IsNonLatin(string compact) => compact.Any(c => c > 0x7F && char.IsLetter(c));

    static readonly Dictionary<char, string> KanaSingles = BuildKanaTable(
        "あ:a い:i う:u え:e お:o か:ka き:ki く:ku け:ke こ:ko が:ga ぎ:gi ぐ:gu げ:ge ご:go "
        + "さ:sa し:shi す:su せ:se そ:so ざ:za じ:ji ず:zu ぜ:ze ぞ:zo た:ta ち:chi つ:tsu て:te と:to "
        + "だ:da ぢ:ji づ:zu で:de ど:do な:na に:ni ぬ:nu ね:ne の:no は:ha ひ:hi ふ:fu へ:he ほ:ho "
        + "ば:ba び:bi ぶ:bu べ:be ぼ:bo ぱ:pa ぴ:pi ぷ:pu ぺ:pe ぽ:po ま:ma み:mi む:mu め:me も:mo "
        + "や:ya ゆ:yu よ:yo ら:ra り:ri る:ru れ:re ろ:ro わ:wa ゐ:i ゑ:e を:o ん:n ゔ:vu "
        + "ぁ:a ぃ:i ぅ:u ぇ:e ぉ:o ゃ:ya ゅ:yu ょ:yo ゎ:wa ゕ:ka ゖ:ke");

    static readonly Dictionary<string, string> KanaDigraphs = new(StringComparer.Ordinal) {
        ["きゃ"] = "kya", ["きゅ"] = "kyu", ["きょ"] = "kyo", ["ぎゃ"] = "gya", ["ぎゅ"] = "gyu", ["ぎょ"] = "gyo",
        ["しゃ"] = "sha", ["しゅ"] = "shu", ["しょ"] = "sho", ["じゃ"] = "ja", ["じゅ"] = "ju", ["じょ"] = "jo",
        ["ちゃ"] = "cha", ["ちゅ"] = "chu", ["ちょ"] = "cho", ["ぢゃ"] = "ja", ["ぢゅ"] = "ju", ["ぢょ"] = "jo",
        ["にゃ"] = "nya", ["にゅ"] = "nyu", ["にょ"] = "nyo", ["ひゃ"] = "hya", ["ひゅ"] = "hyu", ["ひょ"] = "hyo",
        ["びゃ"] = "bya", ["びゅ"] = "byu", ["びょ"] = "byo", ["ぴゃ"] = "pya", ["ぴゅ"] = "pyu", ["ぴょ"] = "pyo",
        ["みゃ"] = "mya", ["みゅ"] = "myu", ["みょ"] = "myo", ["りゃ"] = "rya", ["りゅ"] = "ryu", ["りょ"] = "ryo",
        ["しぇ"] = "she", ["じぇ"] = "je", ["ちぇ"] = "che", ["つぁ"] = "tsa", ["つぇ"] = "tse", ["つぉ"] = "tso",
        ["てぃ"] = "ti", ["でぃ"] = "di", ["とぅ"] = "tu", ["どぅ"] = "du", ["でゅ"] = "dyu",
        ["ふぁ"] = "fa", ["ふぃ"] = "fi", ["ふぇ"] = "fe", ["ふぉ"] = "fo", ["ふゅ"] = "fyu",
        ["うぃ"] = "wi", ["うぇ"] = "we", ["うぉ"] = "wo", ["いぇ"] = "ye",
        ["ゔぁ"] = "va", ["ゔぃ"] = "vi", ["ゔぇ"] = "ve", ["ゔぉ"] = "vo",
    };

    static Dictionary<char, string> BuildKanaTable(string pairs)
        => pairs.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split(':'))
            .ToDictionary(p => p[0][0], p => p[1]);

    static char ToHiragana(char c) => c is >= 'ァ' and <= 'ヶ' ? (char)(c - 0x60) : c;

    /// <summary>Hepburn romanization of hiragana and katakana; any other character is kept as is.</summary>
    internal static string KanaToRomaji(string text) {
        var sb = new StringBuilder(text.Length * 2);
        bool doubleNextConsonant = false;

        for (int i = 0; i < text.Length; i++) {
            char c = ToHiragana(text[i]);

            if (c == 'っ') {
                doubleNextConsonant = true;
                continue;
            }
            if (c == 'ー')
                continue; // long vowel mark: vowel length is ignored anyway

            string? romaji = null;
            if (i + 1 < text.Length && KanaDigraphs.TryGetValue($"{c}{ToHiragana(text[i + 1])}", out romaji))
                i++;
            else if (!KanaSingles.TryGetValue(c, out romaji))
                romaji = null;

            if (romaji == null) {
                sb.Append(text[i]);
            } else {
                if (doubleNextConsonant && !"aiueon".Contains(romaji[0]))
                    sb.Append(romaji.StartsWith("ch", StringComparison.Ordinal) ? 't' : romaji[0]);
                sb.Append(romaji);
            }

            doubleNextConsonant = false;
        }

        return sb.ToString();
    }

    #endregion

    #region String helpers

    static bool ContainsMeaningfully(string a, string b) {
        if (a.Length == 0 || b.Length == 0)
            return false;
        (string shorter, string longer) = a.Length <= b.Length ? (a, b) : (b, a);
        return shorter.Length >= 4 && shorter.Length * 2 >= longer.Length && longer.Contains(shorter, StringComparison.Ordinal);
    }

    internal static double Similarity(string a, string b) {
        if (a.Length == 0 || b.Length == 0)
            return 0;
        if (a == b)
            return 1;
        return 1.0 - (double)Levenshtein(a, b) / Math.Max(a.Length, b.Length);
    }

    static int Levenshtein(string a, string b) {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++)
            previous[j] = j;

        for (int i = 1; i <= a.Length; i++) {
            current[0] = i;
            for (int j = 1; j <= b.Length; j++) {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }
            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }

    static string StripQuotes(string text) => text.Replace("\"", string.Empty);

    static string CollapseSpaces(string text) => Regex.Replace(text, @"\s+", " ").Trim();

    #endregion
}
