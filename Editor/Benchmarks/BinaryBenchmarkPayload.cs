#if UNITY_EDITOR
using UnityEngine;

// Experimental container only. Production payload serialization is unchanged.
[PreferBinarySerialization]
public sealed class BinaryBenchmarkPayload : NativeMeshPayloadAsset { }
#endif
