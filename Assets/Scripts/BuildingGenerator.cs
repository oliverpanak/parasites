using UnityEngine;
using System.Collections.Generic;

#if UNITY_EDITOR
using UnityEditor;
#endif

[System.Serializable]
public class WeightedPrefab
{
    public GameObject prefab;
    [Min(0.01f)] public float weight = 1f;
}

public class BuildingGenerator : MonoBehaviour
{
    [Header("Bottom Concrete Blocks")]
    public WeightedPrefab[] bottomConcreteBlocks;

    [Header("Top Concrete Blocks")]
    public WeightedPrefab[] topConcreteBlocks;

    [Header("Window Blocks")]
    public WeightedPrefab[] windowBlocks;

    [Header("Pillar Blocks")]
    public WeightedPrefab[] pillarBlocks;

    [Header("Slanted Blocks (Rooftop)")]
    public WeightedPrefab[] slantedBlocks;

    [Header("Rubble Blocks")]
    public WeightedPrefab[] rubbleBlocks;

    [Header("Building Dimensions (Bounding Box)")]
    [Min(3)] public int width = 10;
    [Min(3)] public int depth = 10;
    [Min(5)] public int height = 20;

    [Header("Shape")]
    [Tooltip("How much the building narrows toward the top")]
    [Range(0f, 1f)] public float taperStrength = 0.3f;

    [Tooltip("How rough/irregular the building edges are")]
    [Range(0f, 1f)] public float irregularity = 0.4f;

    [Tooltip("Noise frequency — lower = smoother curves, higher = more jagged")]
    public float noiseScale = 0.25f;

    [Tooltip("Min floors per building section")]
    [Min(2)] public int minSectionHeight = 3;

    [Tooltip("Max floors per building section")]
    [Min(2)] public int maxSectionHeight = 6;

    [Tooltip("Chance a section expands outward instead of shrinking")]
    [Range(0f, 0.5f)] public float overhangChance = 0.2f;

    [Tooltip("Chance per surface block to sprout a balcony")]
    [Range(0f, 0.15f)] public float balconyChance = 0.03f;

    [Tooltip("Max outward extent of balconies in blocks")]
    [Range(1, 3)] public int maxBalconyDepth = 2;

    [Header("Window Settings")]
    [Tooltip("Solid blocks between each window")]
    [Min(1)] public int windowSpacing = 3;

    [Tooltip("Window size in blocks (width x height)")]
    public Vector2Int windowSize = new Vector2Int(1, 2);

    [Header("Layout")]
    [Tooltip("Solid concrete rows at the base")]
    [Min(1)] public int groundFloorHeight = 2;

    [Tooltip("Rows at the top of each column for rubble/slanted blocks")]
    [Min(0)] public int rooftopRows = 2;

    [Tooltip("0 = all slanted, 1 = all rubble")]
    [Range(0f, 1f)] public float rubbleVsSlantedRatio = 0.5f;

    [Header("Seed")]
    public int seed = 42;
    public bool randomizeSeed;

    private System.Random rng;
    private bool[,,] grid;
    private int[,] columnTopY;

    private struct Section
    {
        public int startY, endY;
        public int insetLeft, insetRight, insetFront, insetBack;
    }

    private List<Section> sections;

    // ════════════════════════════════════════════════
    //  Public API
    // ════════════════════════════════════════════════

    public void Generate()
    {
        ClearBuilding();

        if (randomizeSeed)
            seed = Random.Range(0, int.MaxValue);
        rng = new System.Random(seed);

        BuildOccupancyGrid();
        ComputeColumnTopY();
        PlaceAllBlocks();
    }

    public void ClearBuilding()
    {
        for (int i = transform.childCount - 1; i >= 0; i--)
            DestroyImmediate(transform.GetChild(i).gameObject);
    }

    // ════════════════════════════════════════════════
    //  Shape generation
    // ════════════════════════════════════════════════

    private void BuildOccupancyGrid()
    {
        grid = new bool[width, height, depth];

        GenerateSections();

        float nOffL = (float)(rng.NextDouble() * 1000);
        float nOffR = (float)(rng.NextDouble() * 1000);
        float nOffF = (float)(rng.NextDouble() * 1000);
        float nOffB = (float)(rng.NextDouble() * 1000);

        float amp = irregularity * 3f;

        for (int si = 0; si < sections.Count; si++)
        {
            Section sec = sections[si];

            for (int y = sec.startY; y < sec.endY && y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    for (int z = 0; z < depth; z++)
                    {
                        float baseMinX = sec.insetLeft;
                        float baseMaxX = width - 1 - sec.insetRight;
                        float baseMinZ = sec.insetFront;
                        float baseMaxZ = depth - 1 - sec.insetBack;

                        if (baseMinX > baseMaxX || baseMinZ > baseMaxZ)
                            continue;

                        float nL = (Mathf.PerlinNoise(z * noiseScale + nOffL, y * noiseScale * 0.3f) - 0.5f) * amp;
                        float nR = (Mathf.PerlinNoise(z * noiseScale + nOffR, y * noiseScale * 0.3f) - 0.5f) * amp;
                        float nF = (Mathf.PerlinNoise(x * noiseScale + nOffF, y * noiseScale * 0.3f) - 0.5f) * amp;
                        float nB = (Mathf.PerlinNoise(x * noiseScale + nOffB, y * noiseScale * 0.3f) - 0.5f) * amp;

                        float minX = baseMinX + nL;
                        float maxX = baseMaxX - nR;
                        float minZ = baseMinZ + nF;
                        float maxZ = baseMaxZ - nB;

                        if (x >= minX && x <= maxX && z >= minZ && z <= maxZ)
                            grid[x, y, z] = true;
                    }
                }
            }
        }

        AddBalconies();
    }

    private void GenerateSections()
    {
        sections = new List<Section>();

        int currentY = 0;
        int iL = 0, iR = 0, iF = 0, iB = 0;

        while (currentY < height)
        {
            int secH = rng.Next(minSectionHeight, Mathf.Max(minSectionHeight, maxSectionHeight) + 1);
            secH = Mathf.Min(secH, height - currentY);

            if (currentY > 0)
            {
                float t = (float)currentY / height;
                int maxChange = Mathf.Max(1, Mathf.CeilToInt(t * taperStrength * 4f));

                iL = AdjustInset(iL, maxChange);
                iR = AdjustInset(iR, maxChange);
                iF = AdjustInset(iF, maxChange);
                iB = AdjustInset(iB, maxChange);
            }

            int maxInsetW = (width - 1) / 2;
            int maxInsetD = (depth - 1) / 2;
            iL = Mathf.Clamp(iL, 0, maxInsetW);
            iR = Mathf.Clamp(iR, 0, maxInsetW);
            iF = Mathf.Clamp(iF, 0, maxInsetD);
            iB = Mathf.Clamp(iB, 0, maxInsetD);

            if (iL + iR >= width || iF + iB >= depth)
                break;

            sections.Add(new Section
            {
                startY = currentY,
                endY = currentY + secH,
                insetLeft = iL,
                insetRight = iR,
                insetFront = iF,
                insetBack = iB
            });

            currentY += secH;
        }

        if (sections.Count == 0)
        {
            sections.Add(new Section
            {
                startY = 0, endY = height,
                insetLeft = 0, insetRight = 0,
                insetFront = 0, insetBack = 0
            });
        }
    }

    private int AdjustInset(int current, int maxChange)
    {
        if (rng.NextDouble() < overhangChance)
            return Mathf.Max(0, current - rng.Next(1, 3));
        else
            return current + rng.Next(0, maxChange + 1);
    }

    private void AddBalconies()
    {
        if (balconyChance <= 0f) return;

        bool[,,] additions = new bool[width, height, depth];
        int[] dx = { -1, 1, 0, 0 };
        int[] dz = { 0, 0, -1, 1 };

        for (int y = groundFloorHeight; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                for (int z = 0; z < depth; z++)
                {
                    if (!grid[x, y, z]) continue;
                    if (rng.NextDouble() >= balconyChance) continue;

                    int faceCount = 0;
                    int[] validFaces = new int[4];
                    for (int f = 0; f < 4; f++)
                    {
                        int nx = x + dx[f];
                        int nz = z + dz[f];
                        if (!IsOccupied(nx, y, nz))
                            validFaces[faceCount++] = f;
                    }

                    if (faceCount == 0) continue;

                    int face = validFaces[rng.Next(faceCount)];
                    int fdx = dx[face];
                    int fdz = dz[face];

                    int perpX = fdz != 0 ? 1 : 0;
                    int perpZ = fdx != 0 ? 1 : 0;

                    int extent = rng.Next(1, maxBalconyDepth + 1);
                    int halfWidth = rng.Next(0, 2);

                    for (int d = 1; d <= extent; d++)
                    {
                        int bx = x + fdx * d;
                        int bz = z + fdz * d;
                        if (bx < 0 || bx >= width || bz < 0 || bz >= depth) break;
                        if (grid[bx, y, bz]) break;

                        additions[bx, y, bz] = true;

                        for (int w = 1; w <= halfWidth; w++)
                        {
                            int wx = bx + perpX * w;
                            int wz = bz + perpZ * w;
                            if (wx >= 0 && wx < width && wz >= 0 && wz < depth && !grid[wx, y, wz])
                                additions[wx, y, wz] = true;

                            wx = bx - perpX * w;
                            wz = bz - perpZ * w;
                            if (wx >= 0 && wx < width && wz >= 0 && wz < depth && !grid[wx, y, wz])
                                additions[wx, y, wz] = true;
                        }
                    }
                }
            }
        }

        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                for (int z = 0; z < depth; z++)
                    if (additions[x, y, z])
                        grid[x, y, z] = true;
    }

    private void ComputeColumnTopY()
    {
        columnTopY = new int[width, depth];
        for (int x = 0; x < width; x++)
        {
            for (int z = 0; z < depth; z++)
            {
                columnTopY[x, z] = -1;
                for (int y = height - 1; y >= 0; y--)
                {
                    if (grid[x, y, z])
                    {
                        columnTopY[x, z] = y;
                        break;
                    }
                }
            }
        }
    }

    // ════════════════════════════════════════════════
    //  Block placement
    // ════════════════════════════════════════════════

    private void PlaceAllBlocks()
    {
        int patternW = windowSpacing + windowSize.x;
        int patternH = windowSpacing + windowSize.y;

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                for (int z = 0; z < depth; z++)
                {
                    if (!grid[x, y, z]) continue;

                    bool front = !IsOccupied(x, y, z - 1);
                    bool back  = !IsOccupied(x, y, z + 1);
                    bool left  = !IsOccupied(x - 1, y, z);
                    bool right = !IsOccupied(x + 1, y, z);
                    bool top   = !IsOccupied(x, y + 1, z);

                    if (!front && !back && !left && !right && !top)
                        continue;

                    int u;
                    Quaternion rot;
                    ResolvePrimaryFace(x, z, front, back, left, right, out u, out rot);

                    int localTop = columnTopY[x, z];
                    float heightT = (float)y / Mathf.Max(1, height - 1);

                    GameObject prefab = ChooseBlock(u, y, heightT, localTop, patternW, patternH);
                    if (prefab == null) continue;

                    GameObject block = InstantiateBlock(prefab);
                    block.transform.localPosition = new Vector3(x, y, z);
                    block.transform.localRotation = rot;
                    block.name = $"{prefab.name}_{x}_{y}_{z}";
                }
            }
        }
    }

    private GameObject ChooseBlock(int u, int y, float heightT, int localTop,
        int patternW, int patternH)
    {
        // Rooftop zone: top rows of each column
        if (rooftopRows > 0 && localTop >= 0 && y > localTop - rooftopRows && y >= groundFloorHeight)
            return PickRooftopBlock();

        // Ground floor
        if (y < groundFloorHeight)
            return PickConcreteBlock(heightT);

        // Main section: window / pillar / concrete pattern
        int colInPattern = u % patternW;
        int rowInPattern = (y - groundFloorHeight) % patternH;

        bool inWindowCol = colInPattern >= windowSpacing;
        bool inWindowRow = rowInPattern >= windowSpacing;

        if (inWindowCol && inWindowRow)
            return PickFromList(windowBlocks) ?? PickConcreteBlock(heightT);

        bool isPillarCol = windowSpacing <= 1
            ? colInPattern == 0
            : colInPattern == 0 || colInPattern == windowSpacing - 1;

        if (isPillarCol)
            return PickFromList(pillarBlocks) ?? PickConcreteBlock(heightT);

        return PickConcreteBlock(heightT);
    }

    private void ResolvePrimaryFace(int x, int z,
        bool front, bool back, bool left, bool right,
        out int u, out Quaternion rotation)
    {
        if (front)      { u = x; rotation = Quaternion.identity; }
        else if (right) { u = z; rotation = Quaternion.Euler(0, -90, 0); }
        else if (back)  { u = x; rotation = Quaternion.Euler(0, 180, 0); }
        else if (left)  { u = z; rotation = Quaternion.Euler(0, 90, 0); }
        else            { u = x; rotation = Quaternion.identity; }
    }

    // ════════════════════════════════════════════════
    //  Helpers
    // ════════════════════════════════════════════════

    private bool IsOccupied(int x, int y, int z)
    {
        if (x < 0 || x >= width || y < 0 || y >= height || z < 0 || z >= depth)
            return false;
        return grid[x, y, z];
    }

    private GameObject PickConcreteBlock(float t)
    {
        bool hasBottom = bottomConcreteBlocks != null && bottomConcreteBlocks.Length > 0;
        bool hasTop = topConcreteBlocks != null && topConcreteBlocks.Length > 0;

        if (!hasBottom && !hasTop) return null;
        if (!hasBottom) return PickFromList(topConcreteBlocks);
        if (!hasTop) return PickFromList(bottomConcreteBlocks);

        return rng.NextDouble() < t
            ? PickFromList(topConcreteBlocks)
            : PickFromList(bottomConcreteBlocks);
    }

    private GameObject PickRooftopBlock()
    {
        bool hasSlanted = slantedBlocks != null && slantedBlocks.Length > 0;
        bool hasRubble = rubbleBlocks != null && rubbleBlocks.Length > 0;

        if (!hasSlanted && !hasRubble) return PickConcreteBlock(1f);
        if (!hasSlanted) return PickFromList(rubbleBlocks);
        if (!hasRubble) return PickFromList(slantedBlocks);

        return rng.NextDouble() < rubbleVsSlantedRatio
            ? PickFromList(rubbleBlocks)
            : PickFromList(slantedBlocks);
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

    private GameObject InstantiateBlock(GameObject prefab)
    {
#if UNITY_EDITOR
        GameObject block = (GameObject)PrefabUtility.InstantiatePrefab(prefab, transform);
        if (block == null)
            block = Instantiate(prefab, transform);
        return block;
#else
        return Instantiate(prefab, transform);
#endif
    }
}

#if UNITY_EDITOR
[CustomEditor(typeof(BuildingGenerator))]
public class BuildingGeneratorEditor : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        BuildingGenerator generator = (BuildingGenerator)target;

        EditorGUILayout.Space(10);

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Generate Building", GUILayout.Height(32)))
            {
                Undo.RegisterFullObjectHierarchyUndo(generator.gameObject, "Generate Building");
                generator.Generate();
            }

            if (GUILayout.Button("Clear", GUILayout.Height(32), GUILayout.Width(60)))
            {
                Undo.RegisterFullObjectHierarchyUndo(generator.gameObject, "Clear Building");
                generator.ClearBuilding();
            }
        }
    }
}
#endif
