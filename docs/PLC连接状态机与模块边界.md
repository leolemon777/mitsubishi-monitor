# PLC 连接状态机与模块边界

本文描述当前源码实际执行的连接生命周期。它是 PLC 会话层的权威说明；设备是否应在线、重连退避、塔灯和报警属于设备管理层，不混入 PLC 会话阶段。

## 1. 唯一状态源

- `MitsubishiPlcService.ConnectionSnapshot` 是真实 PLC 连接状态的唯一来源。
- `PlcStatus.IsConnected` 仅是给现有 WPF 绑定使用的投影，由状态提交函数同步更新，业务判断不得反向依赖它。
- 每个快照携带 `Generation`。建立新会话、主动断开、通信故障和释放服务都只允许把代次增加 1。
- 管理层只处理事件携带的快照对象仍与服务当前快照为同一对象的通知，旧事件不得触发重连。

## 2. 会话状态

```text
Disconnected / CommunicationFault
                |
                | 新代次
                v
          TcpConnecting
                |
                v
       ProtocolVerifying
                |
                v
     AwaitingFirstSample
                |
                v
          OnlineFresh

任一非终止状态 -- 新代次 --> Disconnected / CommunicationFault / Disposed
Disposed 为终止状态，不能重新进入连接流程。
```

状态含义：

| 阶段 | 判定依据 |
|---|---|
| `Disconnected` | 用户主动断开，当前没有可用会话 |
| `TcpConnecting` | 正在建立 TCP，会话尚不可用于业务读取 |
| `ProtocolVerifying` | TCP 已建立，正在只读验证 MC 1E 请求/响应 |
| `AwaitingFirstSample` | 协议验证通过，但本代尚无有效温度样本 |
| `OnlineFresh` | 本代已经提交至少一个有效温度样本 |
| `CommunicationFault` | 超时、协议验证或连续读取失败使旧会话失效 |
| `Disposed` | 服务已释放，禁止重新连接 |

重连 `Backoff` 不属于这张状态机。它由 `DeviceManagerService.ReconnectState` 管理，因为它表达的是四台设备的调度策略，而不是某个 PLC Socket/MC 会话的状态。

## 3. 必须保持的并发不变量

1. 连接代次、阶段快照与 `PlcStatus.IsConnected` 投影在 `_sessionSync` 临界区内提交。
2. 状态规则只允许握手顺序前进；禁止同代跳过协议验证、旧代倒灌和跳代。
3. 温度值、样本序号、质量和 `OnlineFresh` 快照原子提交。
4. `PropertyChanged` 或连接事件订阅者即使同步重入 `Disconnect`，外层旧快照也不得覆盖更新代次。
5. 详细状态事件和旧版 bool 事件在发送前重新确认快照仍为当前状态。
6. MC 协议验证只读取配置中的 X 点，不执行任何 PLC 写操作。

## 4. 自动验证

- `PlcConnectionTransitionPolicyTests` 穷举同代和下一代的完整状态矩阵。
- `MitsubishiPlcServiceRecoveryTests` 覆盖连接中断线、阻塞读取换代、硬超时、首样本和回调重入断线。
- `DemoPlcService` 使用同一转移规则，不能再从未连接直接跳到数据新鲜。

这些测试证明软件状态与模拟通信路径，不代表 FX3U、无线网桥或现场温度仪表已经验收。
