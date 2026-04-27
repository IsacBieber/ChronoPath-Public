================================================================================
  Pathfinding Engine Benchmark Data / 寻路引擎性能基准测试数据
================================================================================

[ Test Environment / 测试环境 ]
Graph Nodes / 图网络节点数                     : 28,249
Graph Edges / 图网络连边数                     : 66,722

[ Initialization & Build (Medians) / 构建与更新耗时 (中位数) ]
End-to-End Pipeline / 全链路端到端总耗时       : 2981.75 ms
Full Update Time / 全量构建更新耗时            : 854.399 ms

[ Query Performance (Medians) / 查询性能 (中位数) ]
Single Distance Query / 单次测距查询延迟       : 1.6769 µs
Single Full Path Query / 单次完整路径查询延迟  : 3.091 µs
Dijkstra Baseline / Dijkstra 基线算法查询延迟  : 1034.78 µs
Query Speedup / 查询加速比 (相较于 Dijkstra)   : 335.8x
Single-Thread Throughput / 单线程测距吞吐量    : 596,338 QPS
Multi-Thread Throughput / 多线程并发峰值吞吐量 : 4,777,374 QPS

[ Memory Footprint / 物理内存占用 ]
Total Engine Memory / 引擎总物理内存占用       : 113.61 MB
================================================================================
