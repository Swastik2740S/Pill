namespace DynamicIsland.Animation;

/// <summary>
/// A damped harmonic oscillator, parameterised the way SwiftUI does it:
/// <c>response</c> is the period of the undamped spring in seconds and
/// <c>dampingFraction</c> is the damping ratio (1 = no overshoot, lower = bouncier).
///
/// Retargeting keeps the current velocity, which is what makes interrupted
/// animations feel continuous instead of restarting from zero.
/// </summary>
public sealed class Spring
{
    public Spring(double initial, double response, double dampingFraction, double epsilon)
    {
        Value = initial;
        Target = initial;
        Epsilon = epsilon;
        Configure(response, dampingFraction);
    }

    public double Value { get; set; }
    public double Velocity { get; set; }
    public double Target { get; set; }

    public double Stiffness { get; private set; }
    public double Damping { get; private set; }

    /// <summary>Distance from target (in the value's own units) that counts as settled.</summary>
    public double Epsilon { get; }

    public bool IsAtRest =>
        Math.Abs(Value - Target) < Epsilon && Math.Abs(Velocity) < Epsilon * 10;

    public void Configure(double response, double dampingFraction)
    {
        response = Math.Max(response, 0.01);
        double omega = 2 * Math.PI / response;
        Stiffness = omega * omega;
        Damping = 2 * dampingFraction * omega;
    }

    /// <summary>Add instantaneous velocity, for "pop" and "bump" effects.</summary>
    public void Impulse(double velocity) => Velocity += velocity;

    public void SnapToTarget()
    {
        Value = Target;
        Velocity = 0;
    }

    /// <summary>Semi-implicit Euler step. Stable for the small fixed steps the system uses.</summary>
    internal void Integrate(double dt)
    {
        double acceleration = -Stiffness * (Value - Target) - Damping * Velocity;
        Velocity += acceleration * dt;
        Value += Velocity * dt;
    }
}
