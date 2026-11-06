using System.Globalization;

namespace NexusShot.Core;

/// <summary>One segment of an icon figure, in the icon's 24-unit design grid.</summary>
public abstract record IconSegment;
public sealed record IconLine(Point To) : IconSegment;
public sealed record IconCubic(Point Control1, Point Control2, Point To) : IconSegment;

/// <summary>An elliptical arc exactly as SVG states it. Direct2D takes the same five parameters, so
/// no conversion to curves is needed.</summary>
public sealed record IconArc(Point To, double RadiusX, double RadiusY, double Rotation, bool Large, bool Clockwise)
    : IconSegment;

public sealed record IconFigure(Point Start, IReadOnlyList<IconSegment> Segments, bool Closed);

/// <summary>
/// Parses SVG path data into figures. The icons are authored as SVG strokes so they can be drawn in a
/// browser and ported unchanged; this reads the subset they use - M, L, H, V, C, A and Z, absolute and
/// relative - and throws on anything else, since an icon that silently drops a command is wrong in a
/// way nobody notices until it ships.
/// </summary>
public static class IconPath
{
    public static IReadOnlyList<IconFigure> Parse(string data)
    {
        var figures = new List<IconFigure>();
        var reader = new Reader(data);

        List<IconSegment>? segments = null;
        var start = Point.Zero;
        var current = Point.Zero;
        var command = '\0';

        void Finish(bool closed)
        {
            if (segments is { Count: > 0 }) figures.Add(new IconFigure(start, segments, closed));
            segments = null;
        }

        while (reader.SkipSeparators())
        {
            // Numbers after a command repeat it; after M they are implicit L, as SVG specifies.
            if (reader.AtCommand) command = reader.ReadCommand();
            else if (command is 'M') command = 'L';
            else if (command is 'm') command = 'l';
            else if (command == '\0') throw new FormatException($"Path data starts without a command: {data}");

            var relative = char.IsLower(command);
            var origin = relative ? current : Point.Zero;

            switch (char.ToUpperInvariant(command))
            {
                case 'M':
                    Finish(closed: false);
                    current = start = Offset(origin, reader.Number(), reader.Number());
                    segments = [];
                    break;

                case 'L':
                    current = Offset(origin, reader.Number(), reader.Number());
                    Add(new IconLine(current));
                    break;

                case 'H':
                    current = new Point((relative ? current.X : 0) + reader.Number(), current.Y);
                    Add(new IconLine(current));
                    break;

                case 'V':
                    current = new Point(current.X, (relative ? current.Y : 0) + reader.Number());
                    Add(new IconLine(current));
                    break;

                case 'C':
                    var control1 = Offset(origin, reader.Number(), reader.Number());
                    var control2 = Offset(origin, reader.Number(), reader.Number());
                    current = Offset(origin, reader.Number(), reader.Number());
                    Add(new IconCubic(control1, control2, current));
                    break;

                case 'A':
                    var radiusX = reader.Number();
                    var radiusY = reader.Number();
                    var rotation = reader.Number();
                    var large = reader.Number() != 0;
                    var clockwise = reader.Number() != 0;
                    current = Offset(origin, reader.Number(), reader.Number());
                    Add(new IconArc(current, radiusX, radiusY, rotation, large, clockwise));
                    break;

                case 'Z':
                    Finish(closed: true);
                    current = start;
                    // A figure may continue after Z without a new M; it starts where the last began.
                    segments = [];
                    break;

                default:
                    throw new FormatException($"Unsupported path command '{command}' in: {data}");
            }
        }

        Finish(closed: false);
        return figures;

        void Add(IconSegment segment) => (segments ??= []).Add(segment);
    }

    private static Point Offset(Point origin, double x, double y) => new(origin.X + x, origin.Y + y);

    private sealed class Reader(string data)
    {
        private int _index;

        /// <summary>Skips whitespace and commas; false at the end of the data.</summary>
        public bool SkipSeparators()
        {
            while (_index < data.Length && (char.IsWhiteSpace(data[_index]) || data[_index] == ',')) _index++;
            return _index < data.Length;
        }

        public bool AtCommand => char.IsAsciiLetter(data[_index]);

        public char ReadCommand() => data[_index++];

        /// <summary>A number in SVG's compact grammar, where "1.5.5" is two numbers and "2-3" is two.</summary>
        public double Number()
        {
            SkipSeparators();
            var begin = _index;
            if (_index < data.Length && data[_index] is '-' or '+') _index++;

            var seenDot = false;
            while (_index < data.Length)
            {
                var c = data[_index];
                if (char.IsAsciiDigit(c)) _index++;
                else if (c == '.' && !seenDot) { seenDot = true; _index++; }
                else break;
            }

            if (!double.TryParse(data.AsSpan(begin, _index - begin), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out var value))
                throw new FormatException($"Expected a number at {begin} in: {data}");
            return value;
        }
    }
}
