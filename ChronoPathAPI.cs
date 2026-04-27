using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace ChronoPathEngine
{
    public class ChronoPathAPI : IDisposable
    {
        private const string DLL_NAME = "st_frame"; 

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern bool STF_Initialize(string nodes_csv, string edges_csv);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern bool STF_InitializeFromArrays(
            int n, long[] ids, double[] lats, double[] lons,
            int e, long[] us, long[] vs, double[] ws);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern int STF_GetInternalId(long old_id);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern double STF_QueryDistance(uint start_node, uint end_node);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern unsafe int STF_RetrievePath(
            uint start_node, uint end_node,
            uint* out_path_buffer, int max_buffer_size,
            out double out_total_dist);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern int STF_GetNodeCount();

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern bool STF_GetNodeCoordinates(double[] out_lat, double[] out_lon);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern bool STF_UpdateEdgeWeight(uint u, uint v, double new_weight);

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern bool STF_RebuildAll();

        [DllImport(DLL_NAME, CallingConvention = CallingConvention.Cdecl)]
        private static extern void STF_Dispose();

        private bool _isDisposed = false;

        public ChronoPathAPI(long[] ids, double[] lats, double[] lons, long[] edgeUs, long[] edgeVs, double[] edgeWeights)
        {
            Debug.Log("[ChronoPath] Initializing from arrays.");
            if (!STF_InitializeFromArrays(ids.Length, ids, lats, lons, edgeUs.Length, edgeUs, edgeVs, edgeWeights))
                throw new Exception("[ChronoPath] Native initialization failed. Check data arrays.");
            Debug.Log("[ChronoPath] Initialization complete.");
        }

        public ChronoPathAPI(string nodesCsvPath, string edgesCsvPath)
        {
            Debug.Log("[ChronoPath] Initializing from CSV files.");
            if (!STF_Initialize(nodesCsvPath, edgesCsvPath))
                throw new Exception("[ChronoPath] Native initialization failed. Check CSV paths.");
            Debug.Log("[ChronoPath] Initialization complete.");
        }

        public int GetInternalId(long old_id) => STF_GetInternalId(old_id);
        
        public double QueryDistance(uint startNode, uint endNode) => STF_QueryDistance(startNode, endNode);

        public unsafe int RetrievePath(uint startNode, uint endNode, uint[] pathBuffer, out double totalDist)
        {
            if (pathBuffer == null || pathBuffer.Length == 0)
            {
                totalDist = -1.0;
                return 0;
            }
            fixed (uint* pBuffer = pathBuffer)
            {
                int actualLen = STF_RetrievePath(startNode, endNode, pBuffer, pathBuffer.Length, out totalDist);
                if (actualLen < 0) return 0;
                return actualLen;
            }
        }

        public bool FetchAllCoordinates(out double[] lats, out double[] lons)
        {
            int count = STF_GetNodeCount();
            if (count <= 0)
            {
                lats = new double[0]; lons = new double[0];
                return false;
            }
            lats = new double[count]; lons = new double[count];
            return STF_GetNodeCoordinates(lats, lons);
        }

        public bool UpdateEdgeWeight(uint u, uint v, double weight) => STF_UpdateEdgeWeight(u, v, weight);

        public bool RebuildAll() 
        {
            Debug.Log("[ChronoPath] Rebuilding routing data.");
            bool success = STF_RebuildAll();
            if (success) {
                Debug.Log("[ChronoPath] Rebuild complete.");
            } else {
                Debug.LogError("[ChronoPath] Rebuild failed.");
            }
            return success;
        }

        public int NodeCount => STF_GetNodeCount();

        public unsafe int GetPathZeroGC(uint startNode, uint endNode, ref uint[] pathBuffer, out double totalDist)
        {
            if (pathBuffer == null || pathBuffer.Length == 0) pathBuffer = new uint[8192];
            fixed (uint* pBuffer = pathBuffer)
            {
                int actualLen = STF_RetrievePath(startNode, endNode, pBuffer, pathBuffer.Length, out totalDist);
                if (actualLen < 0) return 0;
                return Math.Min(actualLen, pathBuffer.Length);
            }
        }

        public void Dispose()
        {
            if (!_isDisposed)
            {
                STF_Dispose();
                _isDisposed = true;
                Debug.Log("[ChronoPath] Disposed native resources.");
                GC.SuppressFinalize(this);
            }
        }

    }
}
