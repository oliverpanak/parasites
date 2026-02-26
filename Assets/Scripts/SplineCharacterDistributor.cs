using UnityEngine;
using UnityEngine.Splines;
using Unity.Mathematics;

#if UNITY_EDITOR
using UnityEditor;
#endif

public class SplineCharacterDistributor : MonoBehaviour
{
    [Header("Spline")]
    [Tooltip("The SplineContainer defining the path. If empty, searches this GameObject.")]
    public SplineContainer splineContainer;

    [Tooltip("Which spline in the container to use.")]
    [Min(0)] public int splineIndex;

    [Header("Bottom Characters (Spline Start)")]
    public WeightedPrefab[] bottomCharacters;

    [Header("Top Characters (Spline End)")]
    public WeightedPrefab[] topCharacters;

    [Header("Distribution Along Spline")]
    [Tooltip("World-space distance between character rings along the spline.")]
    [Range(0.1f, 5f)] public float ringSpacing = 0.5f;

    [Header("Distribution Around Spline")]
    [Tooltip("Base radius of the character ring at the spline start.")]
    [Min(0.1f)] public float baseRadius = 3f;

    [Tooltip("Approximate arc distance between characters around each ring.")]
    [Range(0.2f, 3f)] public float characterSpacing = 0.6f;

    [Header("Taper")]
    [Tooltip("Radius multiplier at the spline end. 0 = point, 1 = no taper.")]
    [Range(0f, 1f)] public float taperEndMultiplier = 0.1f;

    [Tooltip("Taper curve. 1 = linear, <1 = tapers early, >1 = tapers late.")]
    [Range(0.1f, 5f)] public float taperPower = 1f;

    [Header("Character Gradient")]
    [Tooltip("Blend curve. 1 = linear, >1 = stays bottom longer, <1 = transitions quickly.")]
    [Range(0.1f, 5f)] public float gradientPower = 1f;

    [Header("Facing")]
    [Tooltip("Fraction of characters that face toward the spline center.")]
    [Range(0f, 1f)] public float facingInwardPercent = 0.7f;

    [Header("Noise / Irregularity")]
    [Tooltip("Max random position offset per character (world units).")]
    [Range(0f, 1f)] public float positionNoise = 0.1f;

    [Tooltip("Max random rotation offset per axis (degrees).")]
    [Range(0f, 30f)] public float rotationNoise = 5f;

    [Header("Seed")]
    public int seed = 42;
    public bool randomizeSeed;

    private System.Random rng;

    // ════════════════════════════════════════════════
    //  Public API
    // ════════════════════════════════════════════════

    public void Generate()
    {
        ClearCharacters();

        if (randomizeSeed)
            seed = UnityEngine.Random.Range(0, int.MaxValue);
        rng = new System.Random(seed);

        if (!ResolveSplineContainer()) return;

        if (splineIndex >= splineContainer.Splines.Count)
        {
            Debug.LogWarning($"Spline index {splineIndex} out of range (container has {splineContainer.Splines.Count} splines).", this);
            return;
        }

        float splineLength = splineContainer.CalculateLength(splineIndex);
        if (splineLength <= Mathf.Epsilon)
        {
            Debug.LogWarning("Spline has zero length.", this);
            return;
        }

        int ringCount = ComputeRingCount(splineLength);

        for (int i = 0; i < ringCount; i++)
        {
            float t = ringCount > 1 ? (float)i / (ringCount - 1) : 0f;

            splineContainer.Evaluate(splineIndex, t,
                out float3 position, out float3 tangent, out float3 up);

            tangent = math.normalizesafe(tangent, new float3(0, 1, 0));
            up = math.normalizesafe(up, new float3(0, 1, 0));

            float radius = ComputeRadiusAtT(t);
            if (radius < 0.01f) continue;

            int charsInRing = ComputeCharactersPerRing(radius);

            PlaceRing(i, t, position, tangent, up, radius, charsInRing);
        }
    }

    public void ClearCharacters()
    {
        for (int i = transform.childCount - 1; i >= 0; i--)
            DestroyImmediate(transform.GetChild(i).gameObject);
    }

    // ════════════════════════════════════════════════
    //  Ring placement
    // ════════════════════════════════════════════════

    private void PlaceRing(int ringIndex, float t,
        float3 center, float3 tangent, float3 up, float radius, int count)
    {
        float3 binormal = math.normalizesafe(math.cross(tangent, up), new float3(1, 0, 0));
        float3 correctedUp = math.normalizesafe(math.cross(binormal, tangent), new float3(0, 1, 0));

        float angleStep = 2f * math.PI / count;
        float ringAngleOffset = (float)(rng.NextDouble() * 2.0 * math.PI);

        for (int j = 0; j < count; j++)
        {
            float angle = ringAngleOffset + j * angleStep;

            float3 radialDir = math.cos(angle) * binormal + math.sin(angle) * correctedUp;
            float3 charPos = center + radialDir * radius;

            bool faceInward = rng.NextDouble() < facingInwardPercent;

            GameObject prefab = PickCharacter(t);
            if (prefab == null) continue;

            Quaternion rotation = ComputeOrientation(tangent, radialDir, faceInward);

            Vector3 worldPos = new Vector3(charPos.x, charPos.y, charPos.z);
            ApplyNoise(ref worldPos, ref rotation);

            GameObject instance = InstantiateCharacter(prefab);
            instance.transform.position = worldPos;
            instance.transform.rotation = rotation;
            instance.name = $"{prefab.name}_r{ringIndex}_c{j}";
        }
    }

    // ════════════════════════════════════════════════
    //  Orientation
    // ════════════════════════════════════════════════

    private Quaternion ComputeOrientation(float3 tangent, float3 radialDir, bool faceInward)
    {
        Vector3 upDir = ((Vector3)(float3)tangent).normalized;
        Vector3 forwardDir = faceInward
            ? -((Vector3)(float3)radialDir).normalized
            :  ((Vector3)(float3)radialDir).normalized;

        // Re-orthogonalize forward against up
        forwardDir = (forwardDir - Vector3.Dot(forwardDir, upDir) * upDir).normalized;

        if (forwardDir.sqrMagnitude < 0.001f)
            forwardDir = Vector3.forward;

        return Quaternion.LookRotation(forwardDir, upDir);
    }

    private void ApplyNoise(ref Vector3 position, ref Quaternion rotation)
    {
        if (positionNoise > 0f)
        {
            position += new Vector3(
                (float)(rng.NextDouble() * 2.0 - 1.0) * positionNoise,
                (float)(rng.NextDouble() * 2.0 - 1.0) * positionNoise,
                (float)(rng.NextDouble() * 2.0 - 1.0) * positionNoise);
        }

        if (rotationNoise > 0f)
        {
            rotation *= Quaternion.Euler(
                (float)(rng.NextDouble() * 2.0 - 1.0) * rotationNoise,
                (float)(rng.NextDouble() * 2.0 - 1.0) * rotationNoise,
                (float)(rng.NextDouble() * 2.0 - 1.0) * rotationNoise);
        }
    }

    // ════════════════════════════════════════════════
    //  Helpers
    // ════════════════════════════════════════════════

    private bool ResolveSplineContainer()
    {
        if (splineContainer == null)
            splineContainer = GetComponent<SplineContainer>();

        if (splineContainer == null)
        {
            Debug.LogWarning("No SplineContainer found. Assign one or add a SplineContainer component.", this);
            return false;
        }
        return true;
    }

    private int ComputeRingCount(float splineLength)
    {
        float spacing = Mathf.Max(0.1f, ringSpacing);
        return Mathf.Max(2, Mathf.FloorToInt(splineLength / spacing) + 1);
    }

    private float ComputeRadiusAtT(float t)
    {
        float taperT = Mathf.Pow(t, taperPower);
        return baseRadius * Mathf.Lerp(1f, taperEndMultiplier, taperT);
    }

    private int ComputeCharactersPerRing(float radius)
    {
        float circumference = 2f * Mathf.PI * radius;
        return Mathf.Max(3, Mathf.RoundToInt(circumference / Mathf.Max(0.2f, characterSpacing)));
    }

    private GameObject PickCharacter(float t)
    {
        bool hasBottom = bottomCharacters != null && bottomCharacters.Length > 0;
        bool hasTop = topCharacters != null && topCharacters.Length > 0;

        if (!hasBottom && !hasTop) return null;
        if (!hasBottom) return PickFromList(topCharacters);
        if (!hasTop) return PickFromList(bottomCharacters);

        float blendT = Mathf.Pow(t, gradientPower);
        return rng.NextDouble() < blendT
            ? PickFromList(topCharacters)
            : PickFromList(bottomCharacters);
    }

    private GameObject PickFromList(WeightedPrefab[] list)
    {
        if (list == null || list.Length == 0) return null;

        float total = 0f;
        for (int i = 0; i < list.Length; i++)
            if (list[i] != null && list[i].prefab != null)
                total += list[i].weight;

        if (total <= 0f) return null;

        float r = (float)(rng.NextDouble() * total);
        float cumulative = 0f;
        for (int i = 0; i < list.Length; i++)
        {
            if (list[i] == null || list[i].prefab == null) continue;
            cumulative += list[i].weight;
            if (r <= cumulative) return list[i].prefab;
        }

        for (int i = list.Length - 1; i >= 0; i--)
            if (list[i] != null && list[i].prefab != null)
                return list[i].prefab;

        return null;
    }

    private GameObject InstantiateCharacter(GameObject prefab)
    {
#if UNITY_EDITOR
        GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, transform);
        if (instance == null)
            instance = Instantiate(prefab, transform);
        return instance;
#else
        return Instantiate(prefab, transform);
#endif
    }
}

#if UNITY_EDITOR
[CustomEditor(typeof(SplineCharacterDistributor))]
public class SplineCharacterDistributorEditor : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        SplineCharacterDistributor distributor = (SplineCharacterDistributor)target;

        EditorGUILayout.Space(10);

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Generate Characters", GUILayout.Height(32)))
            {
                Undo.RegisterFullObjectHierarchyUndo(distributor.gameObject, "Generate Characters");
                distributor.Generate();
            }

            if (GUILayout.Button("Clear", GUILayout.Height(32), GUILayout.Width(60)))
            {
                Undo.RegisterFullObjectHierarchyUndo(distributor.gameObject, "Clear Characters");
                distributor.ClearCharacters();
            }
        }
    }
}
#endif
