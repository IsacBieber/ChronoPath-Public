using UnityEngine;
using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Threading;
using ChronoPathEngine;

namespace ChronoPathEngine.Examples
{
    public class GridShowcase : MonoBehaviour
    {
        [Header("Benchmark Parameters")]
        public int gridWidth = 150;     
        public int gridHeight = 100;     
        public float roadSpacing = 12f; 
        public int carCount = 10000;    
        public float baseSpeed = 120f;
        public float strikeRadius = 80f;

        [Header("Rendering Resources")]
        public Mesh carMeshTemplate;
        public Material chronoCarMat;
        public Material astarCarMat;
        public Material glLineMat;

        public enum EngineType { ChronoPath, AStar }
        private EngineType _currentEngine = EngineType.ChronoPath;

        private ChronoPathAPI _api;
        private Vector3[] _nodePositions; 
        private struct Edge { public int u, v; }
        private Edge[] _edges;

        private bool[] _isEdgeBroken; 
        private bool[] _isNodeSafe;

        private Mesh _roadMesh;
        private Vector3[] _roadVerts;
        private Color32[] _roadColors;

        private HashSet<ulong> _brokenEdgeHashes = new HashSet<ulong>();
        private LineRenderer _vipLine; 

        private List<int> _rerouteQueue = new List<int>();

        private uint[][] _carPaths;
        private uint[][] _carPathsAlt;
        private int[] _carPathLengths; 
        private int[] _carPathIndices;
        private Vector3[] _carPositions;
        private Quaternion[] _carRotations;
        private float[] _carSpeeds;
        private float[] _carCooldowns;
        private int[] _carTargets;
        private int[] _carCurrentNodes; 
        private int[] _carFailCounts; 

        private Matrix4x4[][] _batches;
        
        private volatile bool _isRerouting = false;
        private int _trackedCarIndex = 0;
        private bool _isInitialized = false;

        private Mesh _carMesh;
        private Material _matChrono; 
        private Material _matAStar;  
        private Material _lineMaterial;

        public struct AdjEdge { public int v; public double w; }
        private AdjEdge[][] _adjList;
        private ThreadLocal<FastAStarWorker> _astarWorkers;
        private ThreadLocal<uint[]> _threadLocalBuffer;
        
        private string _chronoResult = "- ms";
        private string _astarResult = "- ms";
        private Color _chronoColor = Color.white;
        private Color _astarColor = Color.white;
        private bool _isBenchmarking = false;
        private CancellationTokenSource _cts = new CancellationTokenSource();
        private Task _backgroundTask;

        void Start()
        {
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = -1;

            AutoSetupAssets();
            GenerateGraph();
            
            _astarWorkers = new ThreadLocal<FastAStarWorker>(() => new FastAStarWorker(gridWidth * gridHeight));
            _threadLocalBuffer = new ThreadLocal<uint[]>(() => new uint[8192]);

            SpawnCars();
            SetupCamera();
            SetupVIPLine();
            
            _api.RebuildAll();
            
            Task.Run(() => {
                Parallel.For(0, 16, i => { 
                    uint[] dummyBuf = _threadLocalBuffer.Value;
                    double dummyDist; 
                    _api.RetrievePath(0u, 1u, dummyBuf, out dummyDist);
                    _astarWorkers.Value.RunAStar(0, 1, ref dummyBuf, _adjList, _nodePositions, _brokenEdgeHashes, _isNodeSafe);
                });
            }).Wait();

            _isInitialized = true; 
        }

        void AutoSetupAssets()
        {
            if (carMeshTemplate != null) _carMesh = carMeshTemplate;
            else { GameObject temp = GameObject.CreatePrimitive(PrimitiveType.Cube); _carMesh = temp.GetComponent<MeshFilter>().sharedMesh; Destroy(temp); }

            if (chronoCarMat != null) _matChrono = new Material(chronoCarMat) { enableInstancing = true };
            else { Shader std = Shader.Find("Standard"); if (std != null) _matChrono = new Material(std) { color = new Color(0f, 0.8f, 1f, 1f), enableInstancing = true }; }

            if (astarCarMat != null) _matAStar = new Material(astarCarMat) { enableInstancing = true };
            else { Shader std = Shader.Find("Standard"); if (std != null) _matAStar = new Material(std) { color = new Color(1f, 0.3f, 0.3f, 1f), enableInstancing = true }; }
        }

        void EnsureLineMaterial() 
        {
            if (_lineMaterial == null) {
                if (glLineMat != null) _lineMaterial = new Material(glLineMat) { hideFlags = HideFlags.HideAndDontSave };
                else {
                    Shader shader = Shader.Find("Hidden/Internal-Colored") ?? Shader.Find("Sprites/Default");
                    if (shader != null) _lineMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
                }
                if (_lineMaterial != null) {
                    _lineMaterial.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                    _lineMaterial.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                    _lineMaterial.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
                    _lineMaterial.SetInt("_ZWrite", 0);
                }
            }
        }

        void SetupCamera()
        {
            Camera cam = GetComponent<Camera>() ?? Camera.main;
            if (cam != null) {
                float mapWidth = gridWidth * roadSpacing;
                float mapHeight = gridHeight * roadSpacing;
                float mapCenterX = mapWidth / 2f;
                float mapCenterZ = mapHeight / 2f;
                
                cam.orthographic = true;
                cam.orthographicSize = mapHeight * 0.55f;
                
                float shiftX = mapWidth * 0.15f;
                float shiftZ = mapHeight * 0.1f;
                
                cam.transform.position = new Vector3(mapCenterX - shiftX - 170, 1000f, mapCenterZ + shiftZ - 76);
                cam.transform.rotation = Quaternion.Euler(90, 0, 0);
                
                cam.backgroundColor = new Color(0.05f, 0.05f, 0.08f); 
                cam.clearFlags = CameraClearFlags.SolidColor; 
                cam.farClipPlane = 30000f; 
            }
        }

        void SetupVIPLine()
        {
            GameObject vipObj = new GameObject("VIP_Line");
            _vipLine = vipObj.AddComponent<LineRenderer>();
            _vipLine.startWidth = 2.5f; _vipLine.endWidth = 2.5f;
            if (glLineMat != null) _vipLine.material = new Material(glLineMat);
            else { Shader lineShader = Shader.Find("Sprites/Default"); if (lineShader != null) _vipLine.material = new Material(lineShader); }
            _vipLine.positionCount = 0; _vipLine.useWorldSpace = true;
        }

        void GenerateGraph()
        {
            int n = gridWidth * gridHeight;
            long[] ids = new long[n]; double[] lats = new double[n]; double[] lons = new double[n];
            _isNodeSafe = new bool[n]; List<AdjEdge>[] tempAdj = new List<AdjEdge>[n];

            for (int i = 0; i < n; i++) {
                ids[i] = i; lons[i] = (i % gridWidth) * roadSpacing; lats[i] = (i / gridWidth) * roadSpacing;
                _isNodeSafe[i] = true; tempAdj[i] = new List<AdjEdge>();
            }

            List<long> us = new List<long>(); List<long> vs = new List<long>(); List<double> ws = new List<double>();
            List<Vector2Int> origEdges = new List<Vector2Int>();

            for (int y = 0; y < gridHeight; y++) {
                for (int x = 0; x < gridWidth; x++) {
                    int u = y * gridWidth + x;
                    if (x < gridWidth - 1) {
                        int v = y * gridWidth + (x + 1); double noise = UnityEngine.Random.Range(0.01f, 1.5f); 
                        us.Add(u); vs.Add(v); ws.Add(roadSpacing + noise); us.Add(v); vs.Add(u); ws.Add(roadSpacing + noise);
                        origEdges.Add(new Vector2Int(u, v));
                    }
                    if (y < gridHeight - 1) {
                        int v = (y + 1) * gridWidth + x; double noise = UnityEngine.Random.Range(0.01f, 1.5f);
                        us.Add(u); vs.Add(v); ws.Add(roadSpacing + noise); us.Add(v); vs.Add(u); ws.Add(roadSpacing + noise);
                        origEdges.Add(new Vector2Int(u, v));
                    }
                }
            }

            _api = new ChronoPathAPI(ids, lats, lons, us.ToArray(), vs.ToArray(), ws.ToArray());

            _nodePositions = new Vector3[n];
            if (_api.FetchAllCoordinates(out double[] outLats, out double[] outLons)) {
                for (int i = 0; i < n; i++) _nodePositions[i] = new Vector3((float)outLons[i], 0f, (float)outLats[i]); 
            }

            List<Edge> validEdges = new List<Edge>();
            foreach(var e in origEdges) {
                int internalU = _api.GetInternalId(e.x); int internalV = _api.GetInternalId(e.y);
                if (internalU != -1 && internalV != -1) {
                    validEdges.Add(new Edge { u = internalU, v = internalV });
                    tempAdj[internalU].Add(new AdjEdge { v = internalV, w = roadSpacing });
                    tempAdj[internalV].Add(new AdjEdge { v = internalU, w = roadSpacing });
                }
            }
            
            _adjList = new AdjEdge[n][];
            for (int i = 0; i < n; i++) _adjList[i] = tempAdj[i].ToArray();

            _edges = validEdges.ToArray(); _isEdgeBroken = new bool[_edges.Length];
            _roadMesh = new Mesh() { indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 }; _roadMesh.MarkDynamic();
            _roadVerts = new Vector3[_edges.Length * 2]; _roadColors = new Color32[_edges.Length * 2]; 
            int[] roadIndices = new int[_edges.Length * 2]; Color32 blueCol = new Color32(38, 76, 128, 120);
            
            for (int i = 0; i < _edges.Length; i++) {
                _roadVerts[i * 2] = _nodePositions[_edges[i].u]; _roadVerts[i * 2 + 1] = _nodePositions[_edges[i].v];
                _roadColors[i * 2] = blueCol; _roadColors[i * 2 + 1] = blueCol;
                roadIndices[i * 2] = i * 2; roadIndices[i * 2 + 1] = i * 2 + 1;
            }
            
            _roadMesh.vertices = _roadVerts; _roadMesh.colors32 = _roadColors; _roadMesh.SetIndices(roadIndices, MeshTopology.Lines, 0);
            _roadMesh.bounds = new Bounds(new Vector3(gridWidth * roadSpacing / 2f, 0f, gridHeight * roadSpacing / 2f), new Vector3(30000f, 30000f, 30000f));
        }

        void SpawnCars()
        {
            _carPaths = new uint[carCount][]; _carPathsAlt = new uint[carCount][]; _carPathLengths = new int[carCount]; _carPathIndices = new int[carCount];
            _carPositions = new Vector3[carCount]; _carRotations = new Quaternion[carCount];
            _carSpeeds = new float[carCount]; _carCooldowns = new float[carCount];
            _carTargets = new int[carCount]; _carCurrentNodes = new int[carCount]; _carFailCounts = new int[carCount];

            for (int i = 0; i < carCount; i++) { _carPaths[i] = new uint[8192]; _carPathsAlt[i] = new uint[8192]; }

            int numBatches = Mathf.CeilToInt(carCount / 1023f);
            _batches = new Matrix4x4[numBatches][];
            for (int i = 0; i < numBatches; i++) _batches[i] = new Matrix4x4[Mathf.Min(1023, carCount - i * 1023)];

            for (int i = 0; i < carCount; i++) PrepareNewRoute(i, true);
            
            EngineType activeEngine = _currentEngine;
            Parallel.For(0, carCount, i => {
                uint[] localBuf = _threadLocalBuffer.Value;
                int len = 0;
                if (activeEngine == EngineType.ChronoPath) {
                    len = _api.RetrievePath((uint)_carCurrentNodes[i], (uint)_carTargets[i], localBuf, out double dummyDist);
                    if (double.IsInfinity(dummyDist)) len = 0;
                } else {
                    len = _astarWorkers.Value.RunAStar(_carCurrentNodes[i], _carTargets[i], ref localBuf, _adjList, _nodePositions, _brokenEdgeHashes, _isNodeSafe);
                }
                if (len > 0) {
                    int safeLen = Math.Min(len, Math.Min(localBuf.Length, _carPaths[i].Length));
                    Array.Copy(localBuf, 0, _carPaths[i], 0, safeLen);
                    _carPathLengths[i] = safeLen; _carFailCounts[i] = 0; 
                } else {
                    _carPathLengths[i] = 0; _carFailCounts[i]++;
                }
                _threadLocalBuffer.Value = localBuf;
            });

            if (carCount > 0) _trackedCarIndex = UnityEngine.Random.Range(0, carCount);
        }

        void PrepareNewRoute(int i, bool isRespawn)
        {
            _carSpeeds[i] = baseSpeed * UnityEngine.Random.Range(0.6f, 1.4f); 
            int s;
            if (isRespawn) {
                long oldS; int attempts = 0;
                do { oldS = UnityEngine.Random.Range(0, gridWidth * gridHeight); } while (!_isNodeSafe[_api.GetInternalId(oldS)] && ++attempts < 50);
                s = _api.GetInternalId(oldS); _carCurrentNodes[i] = s;
                _carPositions[i] = _nodePositions[s] + new Vector3(UnityEngine.Random.Range(-2f, 2f), 0f, UnityEngine.Random.Range(-2f, 2f)); 
                _carRotations[i] = Quaternion.identity;
            } else { s = _carCurrentNodes[i]; }

            int t; long oldT; int tAttempts = 0;
            do { oldT = UnityEngine.Random.Range(0, gridWidth * gridHeight); t = _api.GetInternalId(oldT); } while ((!_isNodeSafe[t] || t == s) && ++tAttempts < 50);
            _carTargets[i] = t;
        }

        void OnGUI()
        {
            if (!Application.isPlaying || !_isInitialized) return;
            GUIStyle boxStyle = new GUIStyle(GUI.skin.box);
            
            GUILayout.BeginArea(new Rect(10, 10, 420, 200), boxStyle);
            
            GUILayout.Label($"Global Routing Benchmark ({carCount} Entities)", new GUIStyle(GUI.skin.label) { fontSize = 14, fontStyle = FontStyle.Bold, normal = new GUIStyleState() { textColor = Color.white } });
            
            string modeName = _currentEngine == EngineType.ChronoPath ? "ChronoPath" : "Baseline A*";
            GUILayout.Label($"Active Engine: {modeName}", new GUIStyle(GUI.skin.label) { fontSize = 13 });
            GUILayout.Space(10);
            
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Run: ChronoPath", GUILayout.Height(30))) SwitchAndBench(EngineType.ChronoPath);
            if (GUILayout.Button("Run: Baseline A*", GUILayout.Height(30))) SwitchAndBench(EngineType.AStar);
            GUILayout.EndHorizontal();
            
            GUILayout.Space(15);
            GUIStyle resStyle = new GUIStyle(GUI.skin.label) { fontSize = 13 };
            resStyle.normal.textColor = _chronoColor; 
            GUILayout.Label($"[ChronoPath] Execution Time: {_chronoResult}", resStyle); 
            GUILayout.Space(2);
            resStyle.normal.textColor = _astarColor; 
            GUILayout.Label($"[Baseline A*] Execution Time: {_astarResult}", resStyle);
            GUILayout.EndArea();

            if (_isRerouting || _isBenchmarking) {
                GUIStyle statusStyle = new GUIStyle(GUI.skin.label) { fontSize = 20, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
                statusStyle.normal.textColor = Color.white; 
                GUI.Label(new Rect(0, Screen.height - 60, Screen.width, 50f), "Processing...", statusStyle);
            }
        }

        void SwitchAndBench(EngineType targetEngine)
        {
            if (_isBenchmarking || _isRerouting) return; 
            _isBenchmarking = true; _currentEngine = targetEngine;
            if (targetEngine == EngineType.ChronoPath) _chronoResult = "Computing..."; else _astarResult = "Computing..."; 
            long testTime = 0; 
            
            for (int i = 0; i < carCount; i++) {
                _carPathLengths[i] = 0; 
                if (_isNodeSafe[_carCurrentNodes[i]]) {
                    int startX = UnityEngine.Random.Range(0, 3), startY = UnityEngine.Random.Range(0, gridHeight);
                    int s = startY * gridWidth + startX; while (!_isNodeSafe[s]) { startY = UnityEngine.Random.Range(0, gridHeight); s = startY * gridWidth + startX; }
                    int targetX = UnityEngine.Random.Range(gridWidth - 3, gridWidth), targetY = UnityEngine.Random.Range(0, gridHeight);
                    int t = targetY * gridWidth + targetX; while (!_isNodeSafe[t]) { targetY = UnityEngine.Random.Range(0, gridHeight); t = targetY * gridWidth + targetX; }
                    _carCurrentNodes[i] = s; _carPositions[i] = _nodePositions[s]; _carTargets[i] = t;
                }
            }

            CancellationToken token = _cts.Token;
            _backgroundTask = Task.Run(() => {
                try {
                    token.ThrowIfCancellationRequested();
                    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                    Stopwatch sw = Stopwatch.StartNew();
                    Parallel.For(0, carCount, new ParallelOptions { CancellationToken = token }, i => {
                        if (_isNodeSafe[_carCurrentNodes[i]]) {
                            uint[] buf = _threadLocalBuffer.Value; int len = 0;
                            if (targetEngine == EngineType.ChronoPath) {
                                len = _api.RetrievePath((uint)_carCurrentNodes[i], (uint)_carTargets[i], buf, out double dummyDist);
                                if (double.IsInfinity(dummyDist)) len = 0;
                            } else {
                                len = _astarWorkers.Value.RunAStar(_carCurrentNodes[i], _carTargets[i], ref buf, _adjList, _nodePositions, _brokenEdgeHashes, _isNodeSafe);
                            }

                            if (len > 0) {
                                int safeLen = Math.Min(len, Math.Min(buf.Length, _carPaths[i].Length));
                                if (_carPathsAlt[i] == null || _carPathsAlt[i].Length < safeLen)
                                    _carPathsAlt[i] = new uint[Math.Max(8192, safeLen)];
                                Array.Copy(buf, 0, _carPathsAlt[i], 0, safeLen);
                                uint[] temp = _carPaths[i];
                                _carPaths[i] = _carPathsAlt[i];
                                _carPathsAlt[i] = temp;
                                _carPathIndices[i] = 0;
                                _carPathLengths[i] = safeLen;
                            }
                            else _carPathLengths[i] = 0;
                            _threadLocalBuffer.Value = buf;
                        }
                    });
                    sw.Stop(); testTime = sw.ElapsedMilliseconds;
                    if (targetEngine == EngineType.ChronoPath) { _chronoResult = $"{testTime} ms"; _chronoColor = Color.cyan; }
                    else { _astarResult = $"{testTime} ms"; _astarColor = new Color(1f, 0.4f, 0f); }

                    for (int i = 0; i < carCount; i++)
                        if (_isNodeSafe[_carCurrentNodes[i]] && _carPathLengths[i] > 0) {
                            _carPathIndices[i] = 0;
                            _carFailCounts[i] = 0;
                        }
                } catch (OperationCanceledException) {
                } catch (AggregateException ae) {
                    bool onlyCanceled = true;
                    foreach (Exception e in ae.InnerExceptions)
                        if (!(e is OperationCanceledException)) { onlyCanceled = false; break; }
                    if (!onlyCanceled)
                        UnityEngine.Debug.LogError($"[GridShowcase] Benchmark worker exception: {ae}");
                } catch (Exception ex) {
                    UnityEngine.Debug.LogError($"[GridShowcase] Benchmark worker exception: {ex}");
                } finally {
                    _isBenchmarking = false;
                }
            }, token);
        }

        private class FastAStarWorker 
        {
            private double[] _gScore; private uint[] _visited; private uint[] _closed;
            private int[] _cameFrom; private uint[] _tempPath; private uint _runId = 0; private FastMinHeap _heap;

            public FastAStarWorker(int nodeCount) {
                _gScore = new double[nodeCount]; _visited = new uint[nodeCount]; _closed = new uint[nodeCount]; 
                _cameFrom = new int[nodeCount]; _tempPath = new uint[8192]; _heap = new FastMinHeap(nodeCount);
            }

            public int RunAStar(int start, int target, ref uint[] outBuf, AdjEdge[][] adj, Vector3[] pos, HashSet<ulong> brokenHashes, bool[] isSafe) 
            {
                if (start == target) return 0;
                if (++_runId == 0) { Array.Clear(_visited, 0, _visited.Length); Array.Clear(_closed, 0, _closed.Length); _runId = 1; }
                _heap.Clear(); _gScore[start] = 0; _visited[start] = _runId;
                _heap.Push(start, Vector3.Distance(pos[start], pos[target]));

                while (_heap.Count > 0) {
                    int curr = _heap.Pop();
                    if (_closed[curr] == _runId) continue; _closed[curr] = _runId;
                    
                    if (curr == target) {
                        int len = 0; int step = target; 
                        while (step != start) {
                            if (len >= _tempPath.Length) Array.Resize(ref _tempPath, _tempPath.Length * 2);
                            _tempPath[len++] = (uint)step; step = _cameFrom[step];
                        }
                        if (len >= _tempPath.Length) Array.Resize(ref _tempPath, _tempPath.Length * 2);
                        _tempPath[len++] = (uint)start;
                        if (outBuf == null || outBuf.Length < len) outBuf = new uint[Math.Max(8192, len)];
                        for (int i = 0; i < len; i++) outBuf[i] = _tempPath[len - 1 - i];
                        return len;
                    }

                    AdjEdge[] edges = adj[curr]; double currG = _gScore[curr];
                    for (int i = 0; i < edges.Length; i++) {
                        int v = edges[i].v;
                        if (!isSafe[v] || _closed[v] == _runId) continue;
                        uint uu = (uint)curr; uint vv = (uint)v;
                        ulong hash = uu < vv ? (((ulong)uu << 32) | vv) : (((ulong)vv << 32) | uu);
                        if (brokenHashes.Contains(hash)) continue;
                        double tenG = currG + edges[i].w;
                        if (_visited[v] != _runId || tenG < _gScore[v]) {
                            _gScore[v] = tenG; _visited[v] = _runId; _cameFrom[v] = curr;
                            _heap.Push(v, tenG + Vector3.Distance(pos[v], pos[target]));
                        }
                    }
                }
                return 0; 
            }
        }

        private class FastMinHeap {
            public int[] elements; public double[] priorities; public int Count;
            public FastMinHeap(int capacity) { elements = new int[capacity]; priorities = new double[capacity]; }
            public void Clear() { Count = 0; }
            public void Push(int item, double priority) {
                if (Count >= elements.Length) { Array.Resize(ref elements, elements.Length * 2); Array.Resize(ref priorities, priorities.Length * 2); }
                int i = Count++;
                while (i > 0) { int p = (i - 1) / 2; if (priorities[p] <= priority) break; elements[i] = elements[p]; priorities[i] = priorities[p]; i = p; }
                elements[i] = item; priorities[i] = priority;
            }
            public int Pop() {
                int ret = elements[0]; Count--; if (Count == 0) return ret;
                int lastItem = elements[Count]; double lastPri = priorities[Count]; int i = 0;
                while (i * 2 + 1 < Count) {
                    int child = i * 2 + 1; if (child + 1 < Count && priorities[child + 1] < priorities[child]) child++;
                    if (lastPri <= priorities[child]) break;
                    elements[i] = elements[child]; priorities[i] = priorities[child]; i = child;
                }
                elements[i] = lastItem; priorities[i] = lastPri; return ret;
            }
        }

        void Update()
        {
            if (!Application.isPlaying || !_isInitialized) return;

            EnsureLineMaterial();
            if (_lineMaterial != null && _roadMesh != null) {
                Graphics.DrawMesh(_roadMesh, Vector3.zero, Quaternion.identity, _lineMaterial, 0);
            }

            if (!_isRerouting && !_isBenchmarking) {
                if (Input.GetMouseButton(0)) {
                    if (Input.mousePosition.x > 450 || Input.mousePosition.y < Screen.height - 220) {
                        Camera cam = GetComponent<Camera>() ?? Camera.main;
                        if (cam != null) {
                            Ray ray = cam.ScreenPointToRay(Input.mousePosition);
                            if (new Plane(Vector3.up, Vector3.zero).Raycast(ray, out float enter)) {
                                Vector3 pt = ray.GetPoint(enter);
                                BreakAreaAsync(pt); 
                            }
                        }
                    }
                }
            }
            
            if (Input.GetMouseButtonDown(1) && carCount > 0) _trackedCarIndex = UnityEngine.Random.Range(0, carCount);

            if (!_isBenchmarking) UpdateCarsMovement();
            
            RenderCars(); UpdateVIPLine();
        }

        void BreakAreaAsync(Vector3 pt)
        {
            _isRerouting = true; float sqrRadius = strikeRadius * strikeRadius;
            for (int i = 0; i < _nodePositions.Length; i++) {
                if (_isNodeSafe[i]) {
                    float dx = pt.x - _nodePositions[i].x, dz = pt.z - _nodePositions[i].z;
                    if (dx * dx + dz * dz <= sqrRadius) _isNodeSafe[i] = false;
                }
            }

            List<Edge> edgesToCut = new List<Edge>(); HashSet<ulong> currentStrikeHashes = new HashSet<ulong>();
            bool meshChanged = false; Color32 redCol = new Color32(255, 0, 0, 255);

            for (int i = 0; i < _edges.Length; i++) {
                if (_isEdgeBroken[i]) continue;
                float dx = pt.x - _nodePositions[_edges[i].u].x, dz = pt.z - _nodePositions[_edges[i].u].z;
                if (dx * dx + dz * dz < sqrRadius) {
                    _isEdgeBroken[i] = true; edgesToCut.Add(_edges[i]);
                    uint uu = (uint)_edges[i].u, vv = (uint)_edges[i].v;
                    currentStrikeHashes.Add(uu < vv ? (((ulong)uu << 32) | vv) : (((ulong)vv << 32) | uu));
                    
                    _roadColors[i * 2] = redCol; _roadColors[i * 2 + 1] = redCol;
                    _roadVerts[i * 2].y = 1.5f; _roadVerts[i * 2 + 1].y = 1.5f; meshChanged = true;
                }
            }

            if (edgesToCut.Count == 0) { _isRerouting = false; return; }

            if (meshChanged) { _roadMesh.vertices = _roadVerts; _roadMesh.colors32 = _roadColors; }
            
            foreach(var e in edgesToCut) {
                uint uu = (uint)e.u, vv = (uint)e.v;
                _brokenEdgeHashes.Add(uu < vv ? (((ulong)uu << 32) | vv) : (((ulong)vv << 32) | uu));
            }

            uint[][] newPaths = new uint[carCount][]; int[] newLengths = new int[carCount];
            bool[] needReroute = new bool[carCount]; bool[] stitchPrevNode = new bool[carCount];
            int[] queryStarts = new int[carCount]; int[] prevNodes = new int[carCount];

            for (int i = 0; i < carCount; i++) {
                if (!_isNodeSafe[_carCurrentNodes[i]]) { _carPathLengths[i] = 0; _carCooldowns[i] = 0f; continue; }
                uint[] currentPath = _carPaths[i];
                int curLen = _carPathLengths[i];
                int curIdx = _carPathIndices[i];
                if (currentPath != null && curLen > 0 && curIdx < curLen - 1) {
                    bool willHit = false;
                    for (int j = curIdx; j < curLen - 1; j++) {
                        uint uu = currentPath[j], vv = currentPath[j+1];
                        ulong hash = uu < vv ? (((ulong)uu << 32) | vv) : (((ulong)vv << 32) | uu);
                        if (currentStrikeHashes.Contains(hash)) { willHit = true; break; }
                    }
                    if (willHit) {
                        needReroute[i] = true;
                        uint currNode = currentPath[curIdx], nextNode = currentPath[curIdx + 1];
                        ulong chash = currNode < nextNode ? (((ulong)currNode << 32) | nextNode) : (((ulong)nextNode << 32) | currNode);
                        if (currentStrikeHashes.Contains(chash) || _brokenEdgeHashes.Contains(chash)) {
                            queryStarts[i] = (int)currNode; stitchPrevNode[i] = false; 
                        } else { queryStarts[i] = (int)nextNode; prevNodes[i] = (int)currNode; stitchPrevNode[i] = true; }
                        _carPathLengths[i] = 0; 
                    }
                }
            }

            EngineType activeEngine = _currentEngine;
            float currTimeSnapshot = Time.time;

            CancellationToken token = _cts.Token;
            _backgroundTask = Task.Run(() => {
                try {
                    token.ThrowIfCancellationRequested();
                    if (activeEngine == EngineType.ChronoPath) {
                        foreach(var e in edgesToCut) {
                            token.ThrowIfCancellationRequested();
                            _api.UpdateEdgeWeight((uint)e.u, (uint)e.v, double.PositiveInfinity);
                            _api.UpdateEdgeWeight((uint)e.v, (uint)e.u, double.PositiveInfinity);
                        }
                        _api.RebuildAll();
                    }
                    Parallel.For(0, carCount, new ParallelOptions { CancellationToken = token }, i => {
                        if (needReroute[i]) {
                            uint[] localBuf = _threadLocalBuffer.Value; int len = 0;
                            if (activeEngine == EngineType.ChronoPath) {
                                len = _api.RetrievePath((uint)queryStarts[i], (uint)_carTargets[i], localBuf, out double dummyDist);
                                if (double.IsInfinity(dummyDist)) len = 0;
                            } else {
                                len = _astarWorkers.Value.RunAStar(queryStarts[i], _carTargets[i], ref localBuf, _adjList, _nodePositions, _brokenEdgeHashes, _isNodeSafe);
                            }
                            if (len > 0) {
                                int maxAllowed = _carPaths[i].Length - (stitchPrevNode[i] ? 1 : 0);
                                int safeLen = Math.Min(len, Math.Min(localBuf.Length, maxAllowed));
                                if (safeLen <= 0) {
                                    _threadLocalBuffer.Value = localBuf;
                                    return;
                                }
                                if (stitchPrevNode[i]) {
                                    uint[] stitched = new uint[safeLen + 1]; stitched[0] = (uint)prevNodes[i];
                                    Array.Copy(localBuf, 0, stitched, 1, safeLen); newPaths[i] = stitched; newLengths[i] = safeLen + 1;
                                } else { newPaths[i] = new uint[safeLen]; Array.Copy(localBuf, 0, newPaths[i], 0, safeLen); newLengths[i] = safeLen; }
                            }
                            _threadLocalBuffer.Value = localBuf;
                        }
                    });

                    token.ThrowIfCancellationRequested();
                    for (int i = 0; i < carCount; i++) {
                        if (needReroute[i]) {
                            if (newLengths[i] > 0) {
                                if (_carPathsAlt[i] == null || _carPathsAlt[i].Length < newLengths[i])
                                    _carPathsAlt[i] = new uint[Math.Max(8192, newLengths[i])];
                                Array.Copy(newPaths[i], 0, _carPathsAlt[i], 0, newLengths[i]);
                                uint[] temp = _carPaths[i];
                                _carPaths[i] = _carPathsAlt[i];
                                _carPathsAlt[i] = temp;
                                _carPathIndices[i] = 0; _carFailCounts[i] = 0;
                                _carPathLengths[i] = newLengths[i];
                            } else {
                                _carPathLengths[i] = 0;
                                _carFailCounts[i]++;
                                _carCooldowns[i] = currTimeSnapshot + 1f + (i % 200) / 100f;
                            }
                        }
                    }
                } catch (OperationCanceledException) {
                } catch (AggregateException ae) {
                    bool onlyCanceled = true;
                    foreach (Exception e in ae.InnerExceptions)
                        if (!(e is OperationCanceledException)) { onlyCanceled = false; break; }
                    if (!onlyCanceled)
                        UnityEngine.Debug.LogError($"[GridShowcase] BreakArea worker exception: {ae}");
                } catch (Exception ex) {
                    UnityEngine.Debug.LogError($"[GridShowcase] BreakArea worker exception: {ex}");
                } finally {
                    _isRerouting = false;
                }
            }, token);
        }

        void UpdateCarsMovement()
        {
            float dt = Time.deltaTime;
            float currTime = Time.time;
            _rerouteQueue.Clear(); 

            for (int i = 0; i < carCount; i++)
            {
                uint[] currentPath = _carPaths[i];
                int curLen = _carPathLengths[i];
                int curIdx = _carPathIndices[i];

                if (currentPath != null && curLen > 0 && curIdx < curLen - 1)
                {
                    uint currNode = currentPath[curIdx]; uint nextNode = currentPath[curIdx + 1];
                    ulong hash = currNode < nextNode ? (((ulong)currNode << 32) | nextNode) : (((ulong)nextNode << 32) | currNode);

                    if (_brokenEdgeHashes.Contains(hash)) {
                        _carPathLengths[i] = 0; _carCooldowns[i] = currTime + UnityEngine.Random.Range(0.5f, 1.5f);
                    } else {
                        Vector3 target = _nodePositions[(int)nextNode]; 
                        
                        float dx = target.x - _carPositions[i].x, dy = target.y - _carPositions[i].y, dz = target.z - _carPositions[i].z;
                        float distSq = dx * dx + dy * dy + dz * dz;

                        if (distSq > 0.0001f) {
                            float dist = Mathf.Sqrt(distSq);
                            Vector3 dir = new Vector3(dx, dy, dz);
                            _carRotations[i] = Quaternion.Slerp(_carRotations[i], Quaternion.LookRotation(dir), dt * 15f);
                            
                            float move = _carSpeeds[i] * dt;
                            if (move >= dist) {
                                _carPositions[i] = target; _carPathIndices[i]++; _carCurrentNodes[i] = (int)nextNode; 
                                if (_carPathIndices[i] == curLen - 1) _carPathLengths[i] = 0;
                            } else {
                                float ratio = move / dist;
                                _carPositions[i].x += dx * ratio; _carPositions[i].y += dy * ratio; _carPositions[i].z += dz * ratio;
                            }
                        } else {
                            _carPositions[i] = target; _carPathIndices[i]++; _carCurrentNodes[i] = (int)nextNode; 
                            if (_carPathIndices[i] == curLen - 1) _carPathLengths[i] = 0;
                        }
                    }
                }
                else 
                {
                    if (!_isRerouting)
                    {
                        if (!_isNodeSafe[_carCurrentNodes[i]] || _carFailCounts[i] >= 3) { PrepareNewRoute(i, true); _rerouteQueue.Add(i); }
                        else if (currTime > _carCooldowns[i]) { PrepareNewRoute(i, false); _rerouteQueue.Add(i); }
                    }
                }
            }

            if (_rerouteQueue.Count > 0) {
                EngineType activeEngine = _currentEngine;
                Parallel.For(0, _rerouteQueue.Count, qIdx => {
                    int i = _rerouteQueue[qIdx];
                    uint[] localBuf = _threadLocalBuffer.Value;
                    int len = 0;

                    if (activeEngine == EngineType.ChronoPath) {
                        len = _api.RetrievePath((uint)_carCurrentNodes[i], (uint)_carTargets[i], localBuf, out double dummyDist);
                        if (double.IsInfinity(dummyDist)) len = 0;
                    } else {
                        len = _astarWorkers.Value.RunAStar(_carCurrentNodes[i], _carTargets[i], ref localBuf, _adjList, _nodePositions, _brokenEdgeHashes, _isNodeSafe);
                    }

                    if (len > 0) {
                        int safeLen = Math.Min(len, Math.Min(localBuf.Length, _carPaths[i].Length));
                        if (_carPathsAlt[i] == null || _carPathsAlt[i].Length < safeLen)
                            _carPathsAlt[i] = new uint[Math.Max(8192, safeLen)];
                        Array.Copy(localBuf, 0, _carPathsAlt[i], 0, safeLen);
                        uint[] temp = _carPaths[i];
                        _carPaths[i] = _carPathsAlt[i];
                        _carPathsAlt[i] = temp;
                        _carPathIndices[i] = 0; _carFailCounts[i] = 0;
                        _carPathLengths[i] = safeLen;
                    } else {
                        _carPathLengths[i] = 0; _carFailCounts[i]++;
                        _carCooldowns[i] = currTime + 1f + (i % 200) / 100f; 
                    }
                    _threadLocalBuffer.Value = localBuf;
                });
            }
        }

        void RenderCars()
        {
            for (int i = 0; i < carCount; i++)
            {
                int bIdx = i / 1023; int lIdx = i % 1023;
                if (!_isNodeSafe[_carCurrentNodes[i]]) {
                    _batches[bIdx][lIdx] = Matrix4x4.TRS(Vector3.down * 1000f, Quaternion.identity, Vector3.zero);
                } else {
                    float scale = (i == _trackedCarIndex) ? 14f : 3.5f;
                    float height = (i == _trackedCarIndex) ? 35f : 10f;
                    float laneOffset = ((i % 5) - 2f) * 1.5f; 
                    Vector3 renderPos = _carPositions[i] + _carRotations[i] * new Vector3(laneOffset, 0, 0);
                    _batches[bIdx][lIdx] = Matrix4x4.TRS(renderPos, _carRotations[i], new Vector3(scale, height, scale));
                }
            }

            if (_carMesh != null && _matChrono != null && _matAStar != null) {
                Material activeMat = _currentEngine == EngineType.ChronoPath ? _matChrono : _matAStar;
                for (int i = 0; i < _batches.Length; i++) {
                    if (_batches[i] != null) Graphics.DrawMeshInstanced(_carMesh, 0, activeMat, _batches[i]);
                }
            }
        }

        void UpdateVIPLine()
        {
            if (_vipLine == null) return;
            Color col = _currentEngine == EngineType.ChronoPath ? Color.green : Color.white;
            _vipLine.startColor = col; _vipLine.endColor = col;

            if (carCount > 0 && _trackedCarIndex >= 0 && _trackedCarIndex < carCount) {
                uint[] currentPath = _carPaths[_trackedCarIndex];
                int curLen = _carPathLengths[_trackedCarIndex];
                int curIdx = _carPathIndices[_trackedCarIndex];
                if (currentPath != null && curLen > 0 && curIdx < curLen - 1) {
                    int remain = curLen - curIdx;
                    _vipLine.positionCount = remain;
                    for (int j = 0; j < remain; j++) {
                        _vipLine.SetPosition(j, _nodePositions[(int)currentPath[curIdx + j]] + Vector3.up * 4f);
                    }
                } else _vipLine.positionCount = 0;
            } else _vipLine.positionCount = 0;
        }

        void OnDestroy() { 
            if (_cts != null) {
                _cts.Cancel();
                if (_backgroundTask != null && !_backgroundTask.IsCompleted) {
                    try { _backgroundTask.Wait(1000); } catch { }
                }
                _cts.Dispose();
                _cts = null;
            }

            if (_api != null) _api.Dispose(); 
            
            if (_roadMesh != null)     UnityEngine.Object.Destroy(_roadMesh);
            if (_lineMaterial != null) UnityEngine.Object.Destroy(_lineMaterial);
            if (_matChrono != null)    UnityEngine.Object.Destroy(_matChrono);
            if (_matAStar != null)     UnityEngine.Object.Destroy(_matAStar);
            if (_vipLine != null && _vipLine.material != null)
                UnityEngine.Object.Destroy(_vipLine.material);

            if (_threadLocalBuffer != null) _threadLocalBuffer.Dispose();
            if (_astarWorkers != null) _astarWorkers.Dispose();
        }
    }
}