using System;

/// <summary>
/// How one kind of a version's secondary-motion chains moves: the PhysBone values its creator tunes per version. Other
/// PhysBone properties (immobile and limit types, collision, end points) belong to the kind itself.
/// </summary>
[Serializable]
public sealed class PhysBoneSettings
{
    public float pull;
    // PhysBone 1.1 shows it as Momentum: how far the chain overshoots and keeps wobbling.
    public float spring;
    public float stiffness;
    public float gravity;
    public float gravityFalloff;
    public float immobile;
    public float maxAngleX;
    public float maxAngleZ;

    // Tuned on Ultirex's muscle physics: follows the body's own movement, firm.
    public static PhysBoneSettings PhysicDefaults() => new PhysBoneSettings
    { pull = .585f, spring = .752f, stiffness = .2f, gravity = .327f, gravityFalloff = .483f, immobile = 1f, maxAngleX = 50.4f, maxAngleZ = 45f };

    // Tuned on Ultirex's squishy parts: squashes under players' hands and springs back.
    public static PhysBoneSettings SquishyDefaults() => new PhysBoneSettings
    { pull = .585f, spring = .915f, stiffness = .2f, gravity = .16f, gravityFalloff = 0f, immobile = .292f, maxAngleX = 34f, maxAngleZ = 0f };

    public bool SameAs(PhysBoneSettings other) => other != null && pull == other.pull && spring == other.spring &&
        stiffness == other.stiffness && gravity == other.gravity && gravityFalloff == other.gravityFalloff &&
        immobile == other.immobile && maxAngleX == other.maxAngleX && maxAngleZ == other.maxAngleZ;

    public void Validate(string kind)
    {
        foreach (var (name, value, min, max) in new[]
        {
            ("pull", pull, 0f, 1f), ("spring", spring, 0f, 1f), ("stiffness", stiffness, 0f, 1f), ("gravity", gravity, -1f, 1f),
            ("gravityFalloff", gravityFalloff, 0f, 1f), ("immobile", immobile, 0f, 1f), ("maxAngleX", maxAngleX, 0f, 180f), ("maxAngleZ", maxAngleZ, 0f, 180f)
        })
            if (!VersionCustomization.Finite(value) || value < min || value > max)
                throw new ArgumentException($"{kind} {name} must be between {min} and {max}.");
    }
}
