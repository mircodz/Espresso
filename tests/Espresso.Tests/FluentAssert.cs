using System;
using Xunit.Sdk;

namespace Espresso.Tests;

/// <summary>
/// A minimal fluent assertion helper (<c>AssertThat(actual).IsEqualTo(expected)</c>) for the climber
/// tests. Equality on doubles is bitwise, so <c>0.0 != -0.0</c> and <c>NaN == NaN</c>, which pins the
/// exact value a computation produces.
/// </summary>
internal static class FluentAssert
{
    public static LongSubject AssertThat(long actual) => new(actual, null);

    public static DoubleSubject AssertThat(double actual) => new(actual, null);

    public static BoolSubject AssertThat(bool actual) => new(actual, null);

    public static ObjectSubject AssertThat(object? actual) => new(actual, null);

    public static MessageBuilder AssertWithMessage(string message) => new(message);

    internal readonly struct MessageBuilder
    {
        private readonly string _message;

        public MessageBuilder(string message) => _message = message;

        public LongSubject That(long actual) => new(actual, _message);

        public DoubleSubject That(double actual) => new(actual, _message);

        public BoolSubject That(bool actual) => new(actual, _message);

        public ObjectSubject That(object? actual) => new(actual, _message);
    }

    private static void Fail(string? message, string expectation, object? actual)
    {
        string text = $"expected {expectation} but was <{actual}>";
        throw new XunitException(message == null ? text : $"{message}: {text}");
    }

    internal readonly struct LongSubject
    {
        private readonly long _actual;
        private readonly string? _message;

        public LongSubject(long actual, string? message)
        {
            _actual = actual;
            _message = message;
        }

        public void IsEqualTo(long expected)
        {
            if (_actual != expected) Fail(_message, $"<{expected}>", _actual);
        }

        public void IsNotEqualTo(long unexpected)
        {
            if (_actual == unexpected) Fail(_message, $"not <{unexpected}>", _actual);
        }

        public void IsAtLeast(long other)
        {
            if (_actual < other) Fail(_message, $"at least <{other}>", _actual);
        }

        public void IsAtMost(long other)
        {
            if (_actual > other) Fail(_message, $"at most <{other}>", _actual);
        }

        public void IsGreaterThan(long other)
        {
            if (_actual <= other) Fail(_message, $"greater than <{other}>", _actual);
        }

        public void IsLessThan(long other)
        {
            if (_actual >= other) Fail(_message, $"less than <{other}>", _actual);
        }
    }

    internal readonly struct DoubleSubject
    {
        private readonly double _actual;
        private readonly string? _message;

        public DoubleSubject(double actual, string? message)
        {
            _actual = actual;
            _message = message;
        }

        private static bool BitwiseEquals(double a, double b)
            => double.IsNaN(a)
                ? double.IsNaN(b)
                : BitConverter.DoubleToInt64Bits(a) == BitConverter.DoubleToInt64Bits(b);

        public void IsEqualTo(double expected)
        {
            if (!BitwiseEquals(_actual, expected)) Fail(_message, $"<{expected:R}>", _actual.ToString("R"));
        }

        public void IsNotEqualTo(double unexpected)
        {
            if (BitwiseEquals(_actual, unexpected)) Fail(_message, $"not <{unexpected:R}>", _actual.ToString("R"));
        }

        public void IsAtLeast(double other)
        {
            if (!(_actual >= other)) Fail(_message, $"at least <{other:R}>", _actual.ToString("R"));
        }

        public void IsAtMost(double other)
        {
            if (!(_actual <= other)) Fail(_message, $"at most <{other:R}>", _actual.ToString("R"));
        }

        public void IsGreaterThan(double other)
        {
            if (!(_actual > other)) Fail(_message, $"greater than <{other:R}>", _actual.ToString("R"));
        }

        public void IsLessThan(double other)
        {
            if (!(_actual < other)) Fail(_message, $"less than <{other:R}>", _actual.ToString("R"));
        }

        public TolerantDoubleComparison IsWithin(double tolerance) => new(_actual, tolerance, _message);
    }

    internal readonly struct TolerantDoubleComparison
    {
        private readonly double _actual;
        private readonly double _tolerance;
        private readonly string? _message;

        public TolerantDoubleComparison(double actual, double tolerance, string? message)
        {
            _actual = actual;
            _tolerance = tolerance;
            _message = message;
        }

        public void Of(double expected)
        {
            if (!(Math.Abs(_actual - expected) <= _tolerance))
            {
                Fail(_message, $"within <{_tolerance:R}> of <{expected:R}>", _actual.ToString("R"));
            }
        }
    }

    internal readonly struct BoolSubject
    {
        private readonly bool _actual;
        private readonly string? _message;

        public BoolSubject(bool actual, string? message)
        {
            _actual = actual;
            _message = message;
        }

        public void IsTrue()
        {
            if (!_actual) Fail(_message, "true", _actual);
        }

        public void IsFalse()
        {
            if (_actual) Fail(_message, "false", _actual);
        }

        public void IsEqualTo(bool expected)
        {
            if (_actual != expected) Fail(_message, $"<{expected}>", _actual);
        }
    }

    internal readonly struct ObjectSubject
    {
        private readonly object? _actual;
        private readonly string? _message;

        public ObjectSubject(object? actual, string? message)
        {
            _actual = actual;
            _message = message;
        }

        public void IsNull()
        {
            if (_actual != null) Fail(_message, "null", _actual);
        }

        public void IsNotNull()
        {
            if (_actual == null) Fail(_message, "not null", _actual);
        }

        public void IsEqualTo(object? expected)
        {
            if (!Equals(_actual, expected)) Fail(_message, $"<{expected}>", _actual);
        }

        public void IsNotEqualTo(object? unexpected)
        {
            if (Equals(_actual, unexpected)) Fail(_message, $"not <{unexpected}>", _actual);
        }

        public void IsSameInstanceAs(object? expected)
        {
            if (!ReferenceEquals(_actual, expected)) Fail(_message, $"same instance as <{expected}>", _actual);
        }

        public void IsNotSameInstanceAs(object? unexpected)
        {
            if (ReferenceEquals(_actual, unexpected)) Fail(_message, $"not same instance as <{unexpected}>", _actual);
        }
    }
}
