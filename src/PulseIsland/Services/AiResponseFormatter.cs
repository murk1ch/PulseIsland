using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace PulseIsland.Services;

/// <summary>
/// Converts raw LLM responses containing LaTeX math (\(...\), \[...\], $$...$$, $...$)
/// and Markdown (**bold**, *italic*, `code`, headings, lists, code fences) into
/// clean human-readable Unicode text and rich WPF FlowDocument formatting.
/// </summary>
public static class AiResponseFormatter
{
    private static readonly Dictionary<string, string> LatexSymbols = new(StringComparer.Ordinal)
    {
        // Lowercase Greek
        ["alpha"] = "α",
        ["beta"] = "β",
        ["gamma"] = "γ",
        ["delta"] = "δ",
        ["epsilon"] = "ε",
        ["varepsilon"] = "ε",
        ["zeta"] = "ζ",
        ["eta"] = "η",
        ["theta"] = "θ",
        ["vartheta"] = "ϑ",
        ["iota"] = "ι",
        ["kappa"] = "κ",
        ["lambda"] = "λ",
        ["mu"] = "μ",
        ["nu"] = "ν",
        ["xi"] = "ξ",
        ["omicron"] = "ο",
        ["pi"] = "π",
        ["varpi"] = "ϖ",
        ["rho"] = "ρ",
        ["varrho"] = "ϱ",
        ["sigma"] = "σ",
        ["varsigma"] = "ς",
        ["tau"] = "τ",
        ["upsilon"] = "υ",
        ["phi"] = "φ",
        ["varphi"] = "φ",
        ["chi"] = "χ",
        ["psi"] = "ψ",
        ["omega"] = "ω",

        // Uppercase Greek
        ["Gamma"] = "Γ",
        ["Delta"] = "Δ",
        ["Theta"] = "Θ",
        ["Lambda"] = "Λ",
        ["Xi"] = "Ξ",
        ["Pi"] = "Π",
        ["Sigma"] = "Σ",
        ["Upsilon"] = "Υ",
        ["Phi"] = "Φ",
        ["Psi"] = "Ψ",
        ["Omega"] = "Ω",

        // Operators & arithmetic
        ["cdot"] = "·",
        ["cdots"] = "⋯",
        ["ldots"] = "…",
        ["dots"] = "…",
        ["times"] = "×",
        ["div"] = "÷",
        ["pm"] = "±",
        ["mp"] = "∓",
        ["ast"] = "∗",
        ["star"] = "⋆",
        ["circ"] = "°",
        ["degree"] = "°",
        ["prime"] = "′",

        // Relations
        ["le"] = "≤",
        ["leq"] = "≤",
        ["leqslant"] = "≤",
        ["ge"] = "≥",
        ["geq"] = "≥",
        ["geqslant"] = "≥",
        ["ne"] = "≠",
        ["neq"] = "≠",
        ["approx"] = "≈",
        ["equiv"] = "≡",
        ["sim"] = "∼",
        ["simeq"] = "≃",
        ["cong"] = "≅",
        ["propto"] = "∝",
        ["ll"] = "≪",
        ["gg"] = "≫",

        // Calculus, sets, logic
        ["infty"] = "∞",
        ["partial"] = "∂",
        ["nabla"] = "∇",
        ["sum"] = "∑",
        ["prod"] = "∏",
        ["int"] = "∫",
        ["iint"] = "∬",
        ["iiint"] = "∭",
        ["oint"] = "∮",
        ["in"] = "∈",
        ["notin"] = "∉",
        ["ni"] = "∋",
        ["subset"] = "⊂",
        ["subseteq"] = "⊆",
        ["supset"] = "⊃",
        ["supseteq"] = "⊇",
        ["cup"] = "∪",
        ["cap"] = "∩",
        ["emptyset"] = "∅",
        ["varnothing"] = "∅",
        ["forall"] = "∀",
        ["exists"] = "∃",
        ["nexists"] = "∄",
        ["neg"] = "¬",
        ["lnot"] = "¬",
        ["land"] = "∧",
        ["wedge"] = "∧",
        ["lor"] = "∨",
        ["vee"] = "∨",
        ["oplus"] = "⊕",
        ["otimes"] = "⊗",
        ["perp"] = "⊥",
        ["parallel"] = "∥",
        ["angle"] = "∠",
        ["triangle"] = "△",
        ["hbar"] = "ℏ",
        ["ell"] = "ℓ",
        ["Re"] = "Re",
        ["Im"] = "Im",

        // Arrows
        ["to"] = "→",
        ["rightarrow"] = "→",
        ["longrightarrow"] = "→",
        ["leftarrow"] = "←",
        ["longleftarrow"] = "←",
        ["leftrightarrow"] = "↔",
        ["Rightarrow"] = "⇒",
        ["implies"] = "⇒",
        ["Leftarrow"] = "⇐",
        ["Leftrightarrow"] = "⇔",
        ["iff"] = "⇔",
        ["mapsto"] = "↦",
        ["uparrow"] = "↑",
        ["downarrow"] = "↓",

        // Common functions (strip leading backslash cleanly)
        ["sin"] = "sin",
        ["cos"] = "cos",
        ["tan"] = "tan",
        ["cot"] = "cot",
        ["tg"] = "tg",
        ["ctg"] = "ctg",
        ["sec"] = "sec",
        ["csc"] = "csc",
        ["arcsin"] = "arcsin",
        ["arccos"] = "arccos",
        ["arctan"] = "arctan",
        ["sinh"] = "sinh",
        ["cosh"] = "cosh",
        ["tanh"] = "tanh",
        ["ln"] = "ln",
        ["log"] = "log",
        ["lg"] = "lg",
        ["exp"] = "exp",
        ["lim"] = "lim",
        ["max"] = "max",
        ["min"] = "min",
        ["sup"] = "sup",
        ["inf"] = "inf",
        ["det"] = "det",
        ["mod"] = "mod",
        ["bmod"] = "mod",
        ["pmod"] = "mod",
        ["gcd"] = "gcd",
        ["lcm"] = "lcm",
        ["arg"] = "arg",
        ["deg"] = "deg",
        ["dim"] = "dim",
        ["ker"] = "ker",

        // Delimiters & spacing
        ["lvert"] = "|",
        ["rvert"] = "|",
        ["lVert"] = "‖",
        ["rVert"] = "‖",
        ["langle"] = "⟨",
        ["rangle"] = "⟩",
        ["lfloor"] = "⌊",
        ["rfloor"] = "⌋",
        ["lceil"] = "⌈",
        ["rceil"] = "⌉",
        ["quad"] = "  ",
        ["qquad"] = "    ",
        ["space"] = " ",
    };

    private static readonly Dictionary<char, char> SuperscriptMap = new()
    {
        ['0'] = '⁰', ['1'] = '¹', ['2'] = '²', ['3'] = '³', ['4'] = '⁴',
        ['5'] = '⁵', ['6'] = '⁶', ['7'] = '⁷', ['8'] = '⁸', ['9'] = '⁹',
        ['+'] = '⁺', ['-'] = '⁻', ['='] = '⁼', ['('] = '⁽', [')'] = '⁾',
        ['n'] = 'ⁿ', ['i'] = 'ⁱ', ['a'] = 'ᵃ', ['b'] = 'ᵇ', ['c'] = 'ᶜ',
        ['d'] = 'ᵈ', ['e'] = 'ᵉ', ['f'] = 'ᶠ', ['g'] = 'ᵍ', ['h'] = 'ʰ',
        ['j'] = 'ʲ', ['k'] = 'ᵏ', ['l'] = 'ˡ', ['m'] = 'ᵐ', ['o'] = 'ᵒ',
        ['p'] = 'ᵖ', ['r'] = 'ʳ', ['s'] = 'ˢ', ['t'] = 'ᵗ', ['u'] = 'ᵘ',
        ['v'] = 'ᵛ', ['w'] = 'ʷ', ['x'] = 'ˣ', ['y'] = 'ʸ', ['z'] = 'ᶻ',
        ['°'] = '°'
    };

    private static readonly Dictionary<char, char> SubscriptMap = new()
    {
        ['0'] = '₀', ['1'] = '₁', ['2'] = '₂', ['3'] = '₃', ['4'] = '₄',
        ['5'] = '₅', ['6'] = '₆', ['7'] = '₇', ['8'] = '₈', ['9'] = '₉',
        ['+'] = '₊', ['-'] = '₋', ['='] = '₌', ['('] = '₍', [')'] = '₎',
        ['a'] = 'ₐ', ['e'] = 'ₑ', ['h'] = 'ₕ', ['i'] = 'ᵢ', ['j'] = 'ⱼ',
        ['k'] = 'ₖ', ['l'] = 'ₗ', ['m'] = 'ₘ', ['n'] = 'ₙ', ['o'] = 'ₒ',
        ['p'] = 'ₚ', ['r'] = 'ᵣ', ['s'] = 'ₛ', ['t'] = 'ₜ', ['u'] = 'ᵤ',
        ['v'] = 'ᵥ', ['x'] = 'ₓ'
    };

    private static readonly Dictionary<string, string> BlackboardBoldMap = new(StringComparer.Ordinal)
    {
        ["R"] = "ℝ", ["N"] = "ℕ", ["Z"] = "ℤ", ["Q"] = "ℚ", ["C"] = "ℂ", ["P"] = "ℙ", ["E"] = "𝔼"
    };

    private static readonly HashSet<string> IgnoredSizingCommands = new(StringComparer.Ordinal)
    {
        "left", "right", "big", "Big", "bigg", "Bigg", "bigl", "bigr", "Bigl", "Bigr",
        "displaystyle", "textstyle", "scriptstyle", "limits", "nolimits"
    };

    private static readonly HashSet<string> UnwrapSingleArgCommands = new(StringComparer.Ordinal)
    {
        "text", "textrm", "textbf", "textit", "mathrm", "mathbf", "mathit", "mathsf",
        "mathtt", "mathcal", "operatorname", "mbox", "hbox", "boxed"
    };

    private static readonly Regex ThinkBlockRegex = new(@"<think>[\s\S]*?(</think>|$)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Removes hidden reasoning blocks (e.g. Qwen3 &lt;think&gt;...&lt;/think&gt;) when displaying final answers.
    /// </summary>
    public static string StripThinkingTags(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
            return string.Empty;

        if (!raw.Contains("<think>", StringComparison.OrdinalIgnoreCase))
            return raw;

        return ThinkBlockRegex.Replace(raw, string.Empty).TrimStart('\r', '\n');
    }

    /// <summary>
    /// Converts raw LaTeX/Markdown LLM output into clean, readable Unicode plain text
    /// suitable for copying to clipboard and plain-text inspection.
    /// </summary>
    public static string FormatToReadablePlainText(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        string text = StripThinkingTags(raw).Replace("\r\n", "\n");
        var sb = new StringBuilder(text.Length);
        var lines = text.Split('\n');
        bool inCodeFence = false;

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            string trimmed = line.TrimStart();

            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                inCodeFence = !inCodeFence;
                continue;
            }

            if (inCodeFence)
            {
                sb.AppendLine(line);
                continue;
            }

            // Strip Markdown heading markers
            string processed = StripHeadingPrefix(line);

            // Convert bullet markers
            if (processed.StartsWith("- ", StringComparison.Ordinal) || processed.StartsWith("* ", StringComparison.Ordinal))
                processed = "• " + processed[2..];

            // Convert LaTeX math segments to readable Unicode
            processed = ConvertMathSegmentsToUnicode(processed);

            // Strip simple markdown emphasis wrappers while keeping text intact
            processed = StripSimpleMarkdown(processed);

            if (i < lines.Length - 1)
                sb.AppendLine(processed);
            else
                sb.Append(processed);
        }

        return sb.ToString().Trim();
    }

    /// <summary>
    /// Converts a single LaTeX math expression (without outer \( \) or $ delimiters)
    /// into clean readable Unicode math notation.
    /// </summary>
    public static string ConvertLatexMathToUnicode(string latex)
    {
        if (string.IsNullOrWhiteSpace(latex))
            return string.Empty;

        string s = latex.Trim();

        // Normalize matrix/cases/aligned environments cleanly
        s = Regex.Replace(s, @"\\begin\{[^}]+\}", string.Empty);
        s = Regex.Replace(s, @"\\end\{[^}]+\}", string.Empty);
        s = s.Replace(@"\\", "; ");
        s = s.Replace("&", " ");

        // Convert recursively from left to right
        int pos = 0;
        string rendered = ParseLatexSegment(s, ref pos, stopAtCloseBrace: false);

        // Clean up extra spaces around parentheses and operators
        rendered = Regex.Replace(rendered, @"[ \t]{2,}", " ");
        rendered = rendered.Replace("( ", "(").Replace(" )", ")");
        return rendered.Trim();
    }

    /// <summary>
    /// Renders an AI response into a styled WPF FlowDocument with:
    /// - formatted LaTeX math (Unicode symbols + true superscript/subscript runs + accent highlight),
    /// - Markdown headings, bold, italic, inline code, fenced code blocks, and bullet lists.
    /// </summary>
    public static FlowDocument BuildFlowDocument(
        string? raw,
        Brush primaryTextBrush,
        Brush accentBrush,
        Brush codeBgBrush,
        Brush subtleBorderBrush,
        double baseFontSize = 13.5)
    {
        var doc = new FlowDocument
        {
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = baseFontSize,
            Foreground = primaryTextBrush,
            PagePadding = new Thickness(0),
            TextAlignment = TextAlignment.Left
        };

        if (string.IsNullOrWhiteSpace(raw))
            return doc;

        string cleaned = StripThinkingTags(raw).Replace("\r\n", "\n");
        var lines = cleaned.Split('\n');

        bool inCodeFence = false;
        var codeBuffer = new StringBuilder();
        bool inDisplayMath = false;
        var displayMathBuffer = new StringBuilder();

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            string trimmed = line.Trim();

            // Fenced code blocks ```
            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                if (!inCodeFence)
                {
                    inCodeFence = true;
                    codeBuffer.Clear();
                }
                else
                {
                    inCodeFence = false;
                    doc.Blocks.Add(BuildCodeBlockParagraph(codeBuffer.ToString().TrimEnd('\n'), primaryTextBrush, codeBgBrush, subtleBorderBrush, baseFontSize - 1.0));
                }
                continue;
            }

            if (inCodeFence)
            {
                codeBuffer.AppendLine(line);
                continue;
            }

            // Multi-line display math \[ ... \] or $$ ... $$
            if (inDisplayMath)
            {
                int endIdx = FindDisplayMathEnd(trimmed, out string endMarker);
                if (endIdx >= 0)
                {
                    if (endIdx > 0)
                        displayMathBuffer.Append(' ').Append(trimmed[..endIdx]);
                    inDisplayMath = false;
                    doc.Blocks.Add(BuildDisplayMathParagraph(displayMathBuffer.ToString(), accentBrush, codeBgBrush, subtleBorderBrush, baseFontSize + 0.5));
                    string remainder = trimmed[(endIdx + endMarker.Length)..].Trim();
                    if (!string.IsNullOrEmpty(remainder))
                        doc.Blocks.Add(BuildFormattedParagraph(remainder, primaryTextBrush, accentBrush, codeBgBrush, baseFontSize));
                }
                else
                {
                    displayMathBuffer.Append(' ').Append(trimmed);
                }
                continue;
            }

            // Single-line display math block: \[...\] or $$...$$
            if ((trimmed.StartsWith(@"\[", StringComparison.Ordinal) && trimmed.EndsWith(@"\]", StringComparison.Ordinal) && trimmed.Length >= 4) ||
                (trimmed.StartsWith("$$", StringComparison.Ordinal) && trimmed.EndsWith("$$", StringComparison.Ordinal) && trimmed.Length >= 4))
            {
                string expr = trimmed[2..^2];
                doc.Blocks.Add(BuildDisplayMathParagraph(expr, accentBrush, codeBgBrush, subtleBorderBrush, baseFontSize + 0.5));
                continue;
            }

            // Start of multi-line display math block
            if (trimmed == @"\[" || trimmed == "$$" ||
                (trimmed.StartsWith(@"\[", StringComparison.Ordinal) && !trimmed.Contains(@"\]", StringComparison.Ordinal)) ||
                (trimmed.StartsWith("$$", StringComparison.Ordinal) && trimmed.IndexOf("$$", 2, StringComparison.Ordinal) < 0))
            {
                inDisplayMath = true;
                displayMathBuffer.Clear();
                displayMathBuffer.Append(trimmed[2..]);
                continue;
            }

            // Empty line spacing
            if (string.IsNullOrWhiteSpace(line))
            {
                if (doc.Blocks.LastBlock is Paragraph lastP)
                    lastP.Margin = new Thickness(lastP.Margin.Left, lastP.Margin.Top, lastP.Margin.Right, 8);
                continue;
            }

            // Horizontal rule ---
            if (trimmed is "---" or "***" or "___")
            {
                doc.Blocks.Add(new Paragraph
                {
                    BorderBrush = subtleBorderBrush,
                    BorderThickness = new Thickness(0, 0, 0, 1),
                    Margin = new Thickness(0, 4, 0, 6),
                    FontSize = 2
                });
                continue;
            }

            // Markdown table block (| col1 | col2 |)
            if (trimmed.StartsWith('|') && trimmed.EndsWith('|') && trimmed.Count(c => c == '|') >= 3)
            {
                var tableLines = new List<string> { trimmed };
                while (i + 1 < lines.Length)
                {
                    string nextTrimmed = lines[i + 1].Trim();
                    if (nextTrimmed.StartsWith('|') && nextTrimmed.EndsWith('|') && nextTrimmed.Count(c => c == '|') >= 2)
                    {
                        tableLines.Add(nextTrimmed);
                        i++;
                    }
                    else
                    {
                        break;
                    }
                }

                if (tableLines.Count >= 2)
                {
                    doc.Blocks.Add(BuildMarkdownTable(tableLines, primaryTextBrush, accentBrush, codeBgBrush, subtleBorderBrush, baseFontSize - 1.0));
                    continue;
                }
            }

            // Markdown headings (#, ##, ###)
            if (TryParseHeading(line, out int level, out string headingText))
            {
                var hp = new Paragraph
                {
                    Margin = new Thickness(0, doc.Blocks.Count > 0 ? 6 : 0, 0, 4),
                    FontWeight = FontWeights.Bold,
                    FontSize = level switch { 1 => baseFontSize + 2.5, 2 => baseFontSize + 1.5, _ => baseFontSize + 0.5 },
                    Foreground = primaryTextBrush
                };
                AppendInlineContent(hp.Inlines, headingText, primaryTextBrush, accentBrush, codeBgBrush, hp.FontSize);
                doc.Blocks.Add(hp);
                continue;
            }

            // Bullet list items (- item or * item)
            string leadTrimmed = line.TrimStart();
            if ((leadTrimmed.StartsWith("- ", StringComparison.Ordinal) || leadTrimmed.StartsWith("* ", StringComparison.Ordinal)) &&
                !leadTrimmed.StartsWith("---", StringComparison.Ordinal))
            {
                int indentLevel = Math.Min(3, (line.Length - leadTrimmed.Length) / 2);
                var bp = new Paragraph
                {
                    Margin = new Thickness(8 + indentLevel * 12, 1, 0, 3),
                    LineHeight = baseFontSize * 1.48
                };
                bp.Inlines.Add(new Run("• ") { Foreground = accentBrush, FontWeight = FontWeights.Bold });
                AppendInlineContent(bp.Inlines, leadTrimmed[2..], primaryTextBrush, accentBrush, codeBgBrush, baseFontSize);
                doc.Blocks.Add(bp);
                continue;
            }

            // Normal paragraph
            doc.Blocks.Add(BuildFormattedParagraph(line, primaryTextBrush, accentBrush, codeBgBrush, baseFontSize));
        }

        // Flush unclosed fence/math during live streaming so nothing disappears mid-generation
        if (inCodeFence && codeBuffer.Length > 0)
            doc.Blocks.Add(BuildCodeBlockParagraph(codeBuffer.ToString().TrimEnd('\n'), primaryTextBrush, codeBgBrush, subtleBorderBrush, baseFontSize - 1.0));
        if (inDisplayMath && displayMathBuffer.Length > 0)
            doc.Blocks.Add(BuildDisplayMathParagraph(displayMathBuffer.ToString(), accentBrush, codeBgBrush, subtleBorderBrush, baseFontSize + 0.5));

        return doc;
    }

    private static Paragraph BuildFormattedParagraph(
        string text,
        Brush primaryTextBrush,
        Brush accentBrush,
        Brush codeBgBrush,
        double fontSize)
    {
        var p = new Paragraph
        {
            Margin = new Thickness(0, 0, 0, 4),
            LineHeight = fontSize * 1.48
        };
        AppendInlineContent(p.Inlines, text, primaryTextBrush, accentBrush, codeBgBrush, fontSize);
        return p;
    }

    private static Table BuildMarkdownTable(
        List<string> rawRows,
        Brush primaryTextBrush,
        Brush accentBrush,
        Brush codeBgBrush,
        Brush subtleBorderBrush,
        double fontSize)
    {
        var parsedRows = new List<(string[] Cells, bool IsHeader)>();
        for (int r = 0; r < rawRows.Count; r++)
        {
            string s = rawRows[r].Trim();
            if (s.StartsWith('|')) s = s[1..];
            if (s.EndsWith('|')) s = s[..^1];
            string[] cells = s.Split('|').Select(c => c.Trim()).ToArray();
            bool isSeparator = r == 1 && cells.Length > 0 &&
                cells.All(c => c.Length == 0 || c.All(ch => ch is '-' or ':' or ' ')) &&
                cells.Any(c => c.Contains('-'));
            if (isSeparator)
                continue;
            parsedRows.Add((cells, parsedRows.Count == 0));
        }

        int colCount = parsedRows.Count > 0 ? parsedRows.Max(r => r.Cells.Length) : 1;
        var table = new Table
        {
            CellSpacing = 0,
            BorderBrush = subtleBorderBrush,
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 4, 0, 6),
            FontSize = fontSize
        };
        for (int c = 0; c < colCount; c++)
            table.Columns.Add(new TableColumn());

        var rowGroup = new TableRowGroup();
        foreach (var (cells, isHeader) in parsedRows)
        {
            var tr = new TableRow
            {
                Background = isHeader ? codeBgBrush : Brushes.Transparent,
                FontWeight = isHeader ? FontWeights.Bold : FontWeights.Normal
            };

            for (int c = 0; c < colCount; c++)
            {
                string cellText = c < cells.Length ? cells[c] : string.Empty;
                var cellPara = new Paragraph { Margin = new Thickness(0) };
                AppendInlineContent(cellPara.Inlines, cellText, isHeader ? accentBrush : primaryTextBrush, accentBrush, codeBgBrush, fontSize);
                tr.Cells.Add(new TableCell(cellPara)
                {
                    BorderBrush = subtleBorderBrush,
                    BorderThickness = new Thickness(0, 0, c < colCount - 1 ? 1 : 0, 1),
                    Padding = new Thickness(7, 4, 7, 4)
                });
            }
            rowGroup.Rows.Add(tr);
        }

        table.RowGroups.Add(rowGroup);
        return table;
    }

    private static Paragraph BuildCodeBlockParagraph(
        string code,
        Brush primaryTextBrush,
        Brush codeBgBrush,
        Brush subtleBorderBrush,
        double fontSize)
    {
        return new Paragraph(new Run(code))
        {
            FontFamily = new FontFamily("Consolas, Cascadia Mono, Courier New"),
            FontSize = fontSize,
            Foreground = primaryTextBrush,
            Background = codeBgBrush,
            BorderBrush = subtleBorderBrush,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 7, 10, 7),
            Margin = new Thickness(0, 4, 0, 6)
        };
    }

    private static Paragraph BuildDisplayMathParagraph(
        string expr,
        Brush accentBrush,
        Brush codeBgBrush,
        Brush subtleBorderBrush,
        double fontSize)
    {
        var p = new Paragraph
        {
            Background = codeBgBrush,
            BorderBrush = subtleBorderBrush,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 6, 10, 6),
            Margin = new Thickness(0, 4, 0, 6),
            FontFamily = new FontFamily("Cambria Math, Segoe UI Symbol, Segoe UI"),
            FontSize = fontSize,
            Foreground = accentBrush
        };
        AppendLatexMathInlines(p.Inlines, expr, accentBrush, fontSize);
        return p;
    }

    private static void AppendInlineContent(
        InlineCollection inlines,
        string text,
        Brush primaryTextBrush,
        Brush accentBrush,
        Brush codeBgBrush,
        double fontSize)
    {
        int i = 0;
        var plain = new StringBuilder();

        void FlushPlain()
        {
            if (plain.Length == 0) return;
            inlines.Add(new Run(plain.ToString()) { Foreground = primaryTextBrush });
            plain.Clear();
        }

        while (i < text.Length)
        {
            // 1. Inline LaTeX: \( ... \)
            if (i + 1 < text.Length && text[i] == '\\' && text[i + 1] == '(')
            {
                int close = text.IndexOf(@"\)", i + 2, StringComparison.Ordinal);
                if (close > i + 2)
                {
                    FlushPlain();
                    AppendLatexMathInlines(inlines, text[(i + 2)..close], accentBrush, fontSize);
                    i = close + 2;
                    continue;
                }
            }

            // 2. Inline display LaTeX inside a sentence: \[ ... \]
            if (i + 1 < text.Length && text[i] == '\\' && text[i + 1] == '[')
            {
                int close = text.IndexOf(@"\]", i + 2, StringComparison.Ordinal);
                if (close > i + 2)
                {
                    FlushPlain();
                    AppendLatexMathInlines(inlines, text[(i + 2)..close], accentBrush, fontSize);
                    i = close + 2;
                    continue;
                }
            }

            // 3. Dollar LaTeX: $$ ... $$ or $ ... $ (avoid matching currency like $100)
            if (text[i] == '$')
            {
                if (i + 1 < text.Length && text[i + 1] == '$')
                {
                    int close = text.IndexOf("$$", i + 2, StringComparison.Ordinal);
                    if (close > i + 2)
                    {
                        FlushPlain();
                        AppendLatexMathInlines(inlines, text[(i + 2)..close], accentBrush, fontSize);
                        i = close + 2;
                        continue;
                    }
                }
                else if (TryFindClosingSingleDollar(text, i, out int closeDollar))
                {
                    string candidate = text[(i + 1)..closeDollar];
                    if (LooksLikeMath(candidate))
                    {
                        FlushPlain();
                        AppendLatexMathInlines(inlines, candidate, accentBrush, fontSize);
                        i = closeDollar + 1;
                        continue;
                    }
                }
            }

            // 4. Inline code: `code`
            if (text[i] == '`')
            {
                int close = text.IndexOf('`', i + 1);
                if (close > i + 1)
                {
                    FlushPlain();
                    inlines.Add(new Run(" " + text[(i + 1)..close] + " ")
                    {
                        FontFamily = new FontFamily("Consolas, Cascadia Mono, Courier New"),
                        FontSize = Math.Max(11.0, fontSize - 1.0),
                        Background = codeBgBrush,
                        Foreground = accentBrush
                    });
                    i = close + 1;
                    continue;
                }
            }

            // 5. Bold: **text**
            if (i + 1 < text.Length && text[i] == '*' && text[i + 1] == '*')
            {
                int close = text.IndexOf("**", i + 2, StringComparison.Ordinal);
                if (close > i + 2)
                {
                    FlushPlain();
                    var boldSpan = new Span { FontWeight = FontWeights.SemiBold, Foreground = primaryTextBrush };
                    AppendInlineContent(boldSpan.Inlines, text[(i + 2)..close], primaryTextBrush, accentBrush, codeBgBrush, fontSize);
                    inlines.Add(boldSpan);
                    i = close + 2;
                    continue;
                }
            }

            // 6. Italic: *text* (only when not followed by whitespace)
            if (text[i] == '*' && i + 1 < text.Length && !char.IsWhiteSpace(text[i + 1]))
            {
                int close = text.IndexOf('*', i + 1);
                if (close > i + 1 && !char.IsWhiteSpace(text[close - 1]))
                {
                    FlushPlain();
                    var italicSpan = new Span { FontStyle = FontStyles.Italic, Foreground = primaryTextBrush };
                    AppendInlineContent(italicSpan.Inlines, text[(i + 1)..close], primaryTextBrush, accentBrush, codeBgBrush, fontSize);
                    inlines.Add(italicSpan);
                    i = close + 1;
                    continue;
                }
            }

            plain.Append(text[i]);
            i++;
        }

        FlushPlain();
    }

    private static void AppendLatexMathInlines(InlineCollection inlines, string latex, Brush mathBrush, double fontSize)
    {
        string converted = ConvertLatexMathToUnicode(latex);
        if (string.IsNullOrEmpty(converted))
            return;

        // Render any remaining ^{...} or _{...} markers (where Unicode lacked superscript/subscript glyphs)
        // using true WPF BaselineAlignment.Superscript / Subscript runs!
        var mathFont = new FontFamily("Cambria Math, Segoe UI Symbol, Segoe UI");
        int i = 0;
        var normal = new StringBuilder();

        void FlushNormal()
        {
            if (normal.Length == 0) return;
            inlines.Add(new Run(normal.ToString())
            {
                FontFamily = mathFont,
                FontSize = fontSize,
                Foreground = mathBrush
            });
            normal.Clear();
        }

        while (i < converted.Length)
        {
            if ((converted[i] == '^' || converted[i] == '_') && i + 1 < converted.Length)
            {
                bool isSup = converted[i] == '^';
                string scriptText;
                int nextIdx;

                if (converted[i + 1] == '{')
                {
                    int close = FindMatchingBrace(converted, i + 1);
                    if (close > i + 1)
                    {
                        scriptText = converted[(i + 2)..close];
                        nextIdx = close + 1;
                    }
                    else
                    {
                        normal.Append(converted[i]);
                        i++;
                        continue;
                    }
                }
                else
                {
                    scriptText = converted[i + 1].ToString();
                    nextIdx = i + 2;
                }

                FlushNormal();
                inlines.Add(new Run(scriptText)
                {
                    FontFamily = mathFont,
                    FontSize = Math.Max(9.5, fontSize * 0.74),
                    Foreground = mathBrush,
                    BaselineAlignment = isSup ? BaselineAlignment.Superscript : BaselineAlignment.Subscript
                });
                i = nextIdx;
                continue;
            }

            normal.Append(converted[i]);
            i++;
        }

        FlushNormal();
    }

    private static string ParseLatexSegment(string s, ref int i, bool stopAtCloseBrace)
    {
        var sb = new StringBuilder();

        while (i < s.Length)
        {
            char c = s[i];

            if (c == '}' && stopAtCloseBrace)
            {
                i++;
                break;
            }

            if (c == '{')
            {
                i++;
                sb.Append(ParseLatexSegment(s, ref i, stopAtCloseBrace: true));
                continue;
            }

            if (c == '\\')
            {
                i++;
                if (i >= s.Length)
                    break;

                char next = s[i];
                // Escaped punctuation/spaces
                if (!char.IsLetter(next))
                {
                    i++;
                    sb.Append(next switch
                    {
                        ',' or ';' or ':' => " ",
                        '!' => string.Empty,
                        '{' or '}' or '%' or '$' or '#' or '_' or '&' => next.ToString(),
                        _ => next.ToString()
                    });
                    continue;
                }

                int cmdStart = i;
                while (i < s.Length && char.IsLetter(s[i]))
                    i++;
                string cmd = s[cmdStart..i];

                if (IgnoredSizingCommands.Contains(cmd))
                    continue;

                if (cmd is "frac" or "dfrac" or "tfrac")
                {
                    string num = ReadLatexArg(s, ref i);
                    string den = ReadLatexArg(s, ref i);
                    sb.Append(FormatFraction(num, den));
                    continue;
                }

                if (cmd == "sqrt")
                {
                    string? rootIndex = null;
                    SkipSpaces(s, ref i);
                    if (i < s.Length && s[i] == '[')
                    {
                        int closeBracket = s.IndexOf(']', i + 1);
                        if (closeBracket > i + 1)
                        {
                            int tmp = i + 1;
                            rootIndex = ParseLatexSegment(s[..closeBracket], ref tmp, false);
                            i = closeBracket + 1;
                        }
                    }

                    string radicand = ReadLatexArg(s, ref i);
                    string prefix = string.IsNullOrEmpty(rootIndex)
                        ? "√"
                        : (TryToUnicodeScript(rootIndex, SuperscriptMap, out var supRoot) ? supRoot + "√" : $"^{{{rootIndex}}}√");

                    sb.Append(NeedsParensForUnary(radicand) ? $"{prefix}({radicand})" : $"{prefix}{radicand}");
                    continue;
                }

                if (cmd == "mathbb")
                {
                    string arg = ReadLatexArg(s, ref i).Trim();
                    sb.Append(BlackboardBoldMap.TryGetValue(arg, out var bb) ? bb : arg);
                    continue;
                }

                if (cmd is "vec" or "overrightarrow")
                {
                    string arg = ReadLatexArg(s, ref i).Trim();
                    sb.Append(arg.Length == 1 ? arg + "\u20D7" : $"vec({arg})");
                    continue;
                }

                if (cmd is "bar" or "overline")
                {
                    string arg = ReadLatexArg(s, ref i).Trim();
                    sb.Append(arg.Length == 1 ? arg + "\u0304" : $"({arg})\u0304");
                    continue;
                }

                if (cmd is "hat" or "widehat")
                {
                    string arg = ReadLatexArg(s, ref i).Trim();
                    sb.Append(arg.Length == 1 ? arg + "\u0302" : $"hat({arg})");
                    continue;
                }

                if (UnwrapSingleArgCommands.Contains(cmd))
                {
                    sb.Append(ReadLatexArg(s, ref i));
                    continue;
                }

                if (LatexSymbols.TryGetValue(cmd, out string? sym))
                {
                    // Add subtle separator between adjacent words/functions if needed
                    if (sb.Length > 0 && char.IsLetterOrDigit(sb[^1]) && sym.Length > 1 && char.IsLetter(sym[0]))
                        sb.Append(' ');
                    sb.Append(sym);
                    continue;
                }

                // Fallback: unknown LaTeX command without ugly backslash
                sb.Append(cmd);
                continue;
            }

            if (c == '^' || c == '_')
            {
                bool isSup = c == '^';
                i++;
                string arg = ReadLatexArg(s, ref i).Trim();
                var map = isSup ? SuperscriptMap : SubscriptMap;
                if (TryToUnicodeScript(arg, map, out string unicodeScript))
                {
                    sb.Append(unicodeScript);
                }
                else if (arg.Length > 0)
                {
                    // Keep lightweight marker so WPF RichTextBox renders true BaselineAlignment super/subscript
                    sb.Append(isSup ? '^' : '_');
                    sb.Append(arg.Length == 1 ? arg : "{" + arg + "}");
                }
                continue;
            }

            sb.Append(c);
            i++;
        }

        return sb.ToString();
    }

    private static string ReadLatexArg(string s, ref int i)
    {
        SkipSpaces(s, ref i);
        if (i >= s.Length)
            return string.Empty;

        if (s[i] == '{')
        {
            i++;
            return ParseLatexSegment(s, ref i, stopAtCloseBrace: true);
        }

        if (s[i] == '\\')
        {
            int start = i;
            i++;
            while (i < s.Length && char.IsLetter(s[i]))
                i++;
            int subPos = 0;
            return ParseLatexSegment(s[start..i], ref subPos, stopAtCloseBrace: false);
        }

        return s[i++].ToString();
    }

    private static void SkipSpaces(string s, ref int i)
    {
        while (i < s.Length && char.IsWhiteSpace(s[i]))
            i++;
    }

    private static string FormatFraction(string num, string den)
    {
        num = num.Trim();
        den = den.Trim();

        // Compact vulgar fractions for common simple constants
        if (num == "1" && den == "2") return "½";
        if (num == "1" && den == "3") return "⅓";
        if (num == "2" && den == "3") return "⅔";
        if (num == "1" && den == "4") return "¼";
        if (num == "3" && den == "4") return "¾";

        string left = NeedsParensInFraction(num) ? $"({num})" : num;
        string right = NeedsParensInFraction(den) ? $"({den})" : den;
        return $"{left}/{right}";
    }

    private static bool NeedsParensInFraction(string expr)
    {
        if (expr.Length <= 1) return false;
        if (expr.StartsWith('(') && expr.EndsWith(')')) return false;
        return expr.Contains('+') || expr.Contains('-') || expr.Contains(' ') || expr.Contains('/');
    }

    private static bool NeedsParensForUnary(string expr)
    {
        if (expr.Length <= 1) return false;
        if (expr.StartsWith('(') && expr.EndsWith(')')) return false;
        return expr.Contains('+') || expr.Contains('-') || expr.Contains(' ') || expr.Contains('/') || expr.Length > 2;
    }

    private static bool TryToUnicodeScript(string text, Dictionary<char, char> map, out string result)
    {
        if (string.IsNullOrEmpty(text))
        {
            result = string.Empty;
            return false;
        }

        var sb = new StringBuilder(text.Length);
        foreach (char ch in text)
        {
            if (ch == ' ')
                continue;
            if (!map.TryGetValue(ch, out char mapped))
            {
                result = string.Empty;
                return false;
            }
            sb.Append(mapped);
        }

        result = sb.ToString();
        return result.Length > 0;
    }

    private static string ConvertMathSegmentsToUnicode(string text)
    {
        // Replace \[...\], \(...\), $$...$$, $...$ in plain text output
        string result = Regex.Replace(text, @"\\\[(.+?)\\\]", m => ConvertLatexMathToUnicode(m.Groups[1].Value));
        result = Regex.Replace(result, @"\\\((.+?)\\\)", m => ConvertLatexMathToUnicode(m.Groups[1].Value));
        result = Regex.Replace(result, @"\$\$(.+?)\$\$", m => ConvertLatexMathToUnicode(m.Groups[1].Value));
        result = Regex.Replace(result, @"(?<!\$)\$(?!\s)([^\$\n]+?)(?<!\s)\$(?!\$)", m =>
            LooksLikeMath(m.Groups[1].Value) ? ConvertLatexMathToUnicode(m.Groups[1].Value) : m.Value);
        // Clean residual ^{...} or _{...} braces in plain text fallback
        result = Regex.Replace(result, @"\^\{([^}]+)\}", "^($1)");
        result = Regex.Replace(result, @"_\{([^}]+)\}", "_($1)");
        return result;
    }

    private static string StripSimpleMarkdown(string text)
    {
        string r = Regex.Replace(text, @"\*\*(.+?)\*\*", "$1");
        r = Regex.Replace(r, @"`([^`]+)`", "$1");
        return r;
    }

    private static string StripHeadingPrefix(string line)
    {
        return TryParseHeading(line, out _, out string content) ? content : line;
    }

    private static bool TryParseHeading(string line, out int level, out string content)
    {
        level = 0;
        content = line;
        string trimmed = line.TrimStart();
        int hashes = 0;
        while (hashes < trimmed.Length && hashes < 6 && trimmed[hashes] == '#')
            hashes++;

        if (hashes > 0 && hashes < trimmed.Length && trimmed[hashes] == ' ')
        {
            level = hashes;
            content = trimmed[(hashes + 1)..].Trim();
            return true;
        }
        return false;
    }

    private static int FindDisplayMathEnd(string trimmed, out string marker)
    {
        int idx = trimmed.IndexOf(@"\]", StringComparison.Ordinal);
        if (idx >= 0)
        {
            marker = @"\]";
            return idx;
        }
        idx = trimmed.IndexOf("$$", StringComparison.Ordinal);
        if (idx >= 0)
        {
            marker = "$$";
            return idx;
        }
        marker = string.Empty;
        return -1;
    }

    private static bool TryFindClosingSingleDollar(string text, int openIdx, out int closeIdx)
    {
        closeIdx = -1;
        if (openIdx + 1 >= text.Length || char.IsWhiteSpace(text[openIdx + 1]))
            return false;

        for (int j = openIdx + 1; j < text.Length; j++)
        {
            if (text[j] == '\n' || text[j] == '\r')
                return false;

            if (text[j] == '$' && (j == 0 || text[j - 1] != '\\'))
            {
                // An unescaped '$' preceded by whitespace starts a new math span, meaning openIdx was currency ($10)
                if (char.IsWhiteSpace(text[j - 1]))
                    return false;
                // Avoid matching if followed immediately by a digit (currency like $10 ... $20)
                if (j + 1 < text.Length && char.IsDigit(text[j + 1]))
                    return false;
                closeIdx = j;
                return true;
            }
        }
        return false;
    }

    private static bool LooksLikeMath(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        // Pure number like "100" or "15.50" is probably money, not math
        if (Regex.IsMatch(s.Trim(), @"^\d+([.,]\d+)?$"))
            return false;
        return s.Contains('\\') || s.Contains('^') || s.Contains('_') || s.Contains('=') ||
               s.Contains('+') || s.Contains('-') || s.Contains('/') || s.Contains('*') ||
               s.Length <= 4;
    }

    private static int FindMatchingBrace(string s, int openBraceIdx)
    {
        int depth = 0;
        for (int i = openBraceIdx; i < s.Length; i++)
        {
            if (s[i] == '{') depth++;
            else if (s[i] == '}')
            {
                depth--;
                if (depth == 0)
                    return i;
            }
        }
        return -1;
    }
}
