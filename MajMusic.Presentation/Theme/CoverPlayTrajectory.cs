#nullable enable

using Avalonia;

namespace MajMusic.Presentation.Theme
{
    /// <summary>Describes the playback affordance's center and half-side length at one point along its shared trajectory.</summary>
    /// <param name="Center">The position in card-local logical pixels.</param>
    /// <param name="HalfSide">Half the button's side length in logical pixels.</param>
    public readonly record struct CoverPlayPose(Point Center, double HalfSide);

    /// <summary>Combines one uninterrupted cubic center path with simultaneous smooth growth and conservative physical-speed bounds.</summary>
    public readonly struct CoverPlayTrajectory
    {
        /// <summary>Retains the exact initial center supplied by the cover's existing inset calculation.</summary>
        private readonly Point _origin;
        /// <summary>Stores the cubic coefficient of the single center trajectory.</summary>
        private readonly Vector _cubic;
        /// <summary>Stores the quadratic coefficient of the single center trajectory.</summary>
        private readonly Vector _quadratic;
        /// <summary>Stores the linear coefficient of the single center trajectory.</summary>
        private readonly Vector _linear;
        /// <summary>Retains the exact initial half-side supplied by the existing button geometry.</summary>
        private readonly double _initialHalfSide;
        /// <summary>Stores the growth over the complete trajectory rather than a separate animation phase.</summary>
        private readonly double _halfSideGrowth;

        /// <summary>Creates a continuous path from the original cover button toward the card center and completely past its right edge.</summary>
        /// <param name="cardSize">The complete card bounds in logical pixels, without changing its layout.</param>
        /// <param name="startCenter">The original button center calculated by the caller; no inset is recalculated here.</param>
        /// <param name="startHalfSide">The original half-side calculated by the caller.</param>
        /// <exception cref="ArgumentOutOfRangeException">The bounds or half-side are not finite and positive, or the center is not finite.</exception>
        public CoverPlayTrajectory(Size cardSize, Point startCenter, double startHalfSide)
        {
            if (!double.IsFinite(cardSize.Width) || !double.IsFinite(cardSize.Height) || cardSize.Width <= 0 || cardSize.Height <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(cardSize), "Card dimensions must be finite and positive.");
            }

            if (!double.IsFinite(startCenter.X) || !double.IsFinite(startCenter.Y))
            {
                throw new ArgumentOutOfRangeException(nameof(startCenter), "The initial center must be finite.");
            }

            if (!double.IsFinite(startHalfSide) || startHalfSide <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(startHalfSide), "The initial half-side must be finite and positive.");
            }

            _origin = startCenter;
            _initialHalfSide = startHalfSide;
            var finalHalfSide = Math.Max(startHalfSide, cardSize.Height * .42);
            _halfSideGrowth = finalHalfSide - startHalfSide;
            var center = new Point(cardSize.Width * .5, cardSize.Height * .5);
            var end = new Point(cardSize.Width + finalHalfSide + 24, center.Y);
            var first = startCenter + (center - startCenter) * .25;
            var second = new Point(center.X + (end.X - center.X) * .06, center.Y);
            var firstLeg = first - startCenter;
            var middleLeg = second - first;
            var finalLeg = end - second;
            _linear = firstLeg * 3;
            _quadratic = (middleLeg - firstLeg) * 3;
            _cubic = finalLeg - middleLeg * 2 + firstLeg;
        }

        /// <summary>Calculates position and size from the same uninterrupted progress value without applying another timing curve.</summary>
        /// <param name="progress">The normalized progress, clamped to the closed interval from zero to one.</param>
        /// <returns>The corresponding center and half-side, including exact endpoint geometry.</returns>
        /// <exception cref="ArgumentOutOfRangeException">Progress is not finite.</exception>
        public CoverPlayPose GetPose(double progress)
        {
            var value = ClampProgress(progress);
            var growth = value * value * (3 - 2 * value);
            return new CoverPlayPose(_origin + ((_cubic * value + _quadratic) * value + _linear) * value,
                _initialHalfSide + _halfSideGrowth * growth);
        }

        /// <summary>Bounds all center and growing-edge motion inside a proposed forward or reverse progress step.</summary>
        /// <param name="from">The first normalized progress value; either direction is supported.</param>
        /// <param name="to">The second normalized progress value; either direction is supported.</param>
        /// <returns>A conservative logical-pixels-per-progress bound usable as bound times absolute progress step divided by elapsed seconds.</returns>
        /// <exception cref="ArgumentOutOfRangeException">Either progress value is not finite.</exception>
        public double GetMaximumEdgeDerivative(double from, double to)
        {
            var first = ClampProgress(from);
            var second = ClampProgress(to);
            var start = Math.Min(first, second);
            var end = Math.Max(first, second);
            var x = MaximumAbsoluteQuadratic(3 * _cubic.X, 2 * _quadratic.X, _linear.X, start, end);
            var y = MaximumAbsoluteQuadratic(3 * _cubic.Y, 2 * _quadratic.Y, _linear.Y, start, end);
            var size = MaximumAbsoluteQuadratic(-6 * _halfSideGrowth, 6 * _halfSideGrowth, 0, start, end);
            // A square corner moves by sqrt(2) times its half-side change; the rounded boundary lies inside that bound.
            return Math.Sqrt(x * x + y * y) + Math.Sqrt(2) * size;
        }

        /// <summary>Finds an exact component bound using the endpoints and the quadratic's only possible interior extremum.</summary>
        /// <param name="quadratic">The coefficient of the squared progress value.</param>
        /// <param name="linear">The coefficient of progress.</param>
        /// <param name="constant">The constant component.</param>
        /// <param name="start">The lower interval endpoint.</param>
        /// <param name="end">The upper interval endpoint.</param>
        /// <returns>The largest absolute component on the closed interval.</returns>
        private static double MaximumAbsoluteQuadratic(double quadratic, double linear, double constant, double start, double end)
        {
            var first = Math.Abs((quadratic * start + linear) * start + constant);
            var last = Math.Abs((quadratic * end + linear) * end + constant);
            var maximum = Math.Max(first, last);
            if (quadratic != 0)
            {
                var vertex = -linear / (2 * quadratic);
                if (vertex > start && vertex < end)
                {
                    maximum = Math.Max(maximum, Math.Abs((quadratic * vertex + linear) * vertex + constant));
                }
            }

            return maximum;
        }

        /// <summary>Constrains valid progress without extending the path beyond its verified derivative bounds.</summary>
        /// <param name="progress">The finite progress supplied by the gesture driver.</param>
        /// <returns>Progress constrained to zero through one.</returns>
        /// <exception cref="ArgumentOutOfRangeException">Progress is not finite.</exception>
        private static double ClampProgress(double progress)
        {
            if (!double.IsFinite(progress))
            {
                throw new ArgumentOutOfRangeException(nameof(progress), "Progress must be finite.");
            }

            return Math.Clamp(progress, 0, 1);
        }
    }
}
