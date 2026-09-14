# 从零编写基于 TCP 的 LSP 服务器传输层 (VB.NET)
## 理解 LSP 的传输规范
VS Code 的 LSP 客户端与你的服务器通信时，使用的是 **JSON-RPC 2.0** 格式。但由于 TCP 是流式传输（没有消息边界），直接发 JSON 会粘包。LSP 规范规定了一套基于 `Content-Length` 的头部封装机制。
一个完整的 LSP 消息在 TCP 网络流中的样子如下：
```text
Content-Length: 123\r\n
\r\n
{ "jsonrpc": "2.0", "method": "initialize", ... }
```
*   **Header (头部)**：包含键值对，以 `\r\n` 分隔。目前 LSP 唯一强制要求的是 `Content-Length`。
*   **分隔符**：头部结束後，必须有一个额外的空行 `\r\n`。
*   **Body (主体)**：紧随其后的 UTF-8 编码的 JSON 字符串，长度必须等于 `Content-Length` 的值。
我们的核心任务就是：**精准地读取这个格式，并精准地构造这个格式发回去。**
---
## 第一步：搭建 TCP 服务器骨架
我们使用 `System.Net.Sockets.TcpListener` 来监听端口。
新建一个 VB.NET 控制台项目，修改 `Program.vb`：
```vb
Imports System.Net
Imports System.Net.Sockets
Imports System.Text
Imports System.Threading.Tasks
Module Program
    Sub Main(args As String())
        ' 1. 监听本地 8080 端口
        Dim listener As New TcpListener(IPAddress.Loopback, 8080)
        listener.Start()
        Console.WriteLine("LSP Server listening on port 8080...")
        While True
            ' 2. 阻塞等待 VS Code 客户端连接
            Dim tcpClient As TcpClient = listener.AcceptTcpClient()
            Console.WriteLine("Client connected!")
            ' 3. 为每个客户端开启一个独立的处理任务
            Task.Run(Sub() HandleClientAsync(tcpClient))
        End While
    End Sub
    Private Async Sub HandleClientAsync(client As TcpClient)
        ' 下一将在这里实现核心的读写逻辑
    End Sub
End Module
```
---
## 第二步：实现 LSP 消息的读取 (解码)
**⚠️ 致命陷阱警告**：千万不要直接用 `StreamReader` 去读 `NetworkStream`！因为 `StreamReader` 会预读取内部缓冲区，这会把属于 JSON Body 的数据提前读走，导致我们后面无法精确按长度截取。必须**逐字节**读取头部，然后再按长度读取主体。
在 `HandleClientAsync` 中添加读取逻辑：
```vb
Private Async Sub HandleClientAsync(client As TcpClient)
    Try
        Using stream As NetworkStream = client.GetStream()
            While client.Connected
                ' 1. 读取 Header 直到遇到空行 (\r\n\r\n)
                Dim headerBuilder As New StringBuilder()
                Dim buffer(0) As Byte ' 逐字节读取
                Dim prevChar As Char = " "c
                Dim currChar As Char = " "c
                While True
                    Dim bytesRead As Integer = Await stream.ReadAsync(buffer, 0, 1)
                    If bytesRead = 0 Then Return ' 连接断开
                    currChar = ChrW(buffer(0))
                    headerBuilder.Append(currChar)
                    ' 检测是否读到 "\r\n\r\n"
                    If headerBuilder.ToString().EndsWith("\r\n\r\n") Then
                        Exit While
                    End If
                End While
                Dim headers As String = headerBuilder.ToString()
                
                ' 2. 从 Header 中解析 Content-Length
                Dim contentLength As Integer = 0
                For Each line As String In headers.Split(New String() {"\r\n"}, StringSplitOptions.RemoveEmptyEntries)
                    If line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase) Then
                        Dim lengthStr As String = line.Substring("Content-Length:".Length).Trim()
                        Integer.TryParse(lengthStr, contentLength)
                        Exit For
                    End If
                Next
                If contentLength = 0 Then Continue While
                ' 3. 精准读取指定长度的 Body (JSON 数据)
                Dim bodyBytes(contentLength - 1) As Byte
                Dim totalRead As Integer = 0
                While totalRead < contentLength
                    Dim read As Integer = Await stream.ReadAsync(bodyBytes, totalRead, contentLength - totalRead)
                    If read = 0 Then Return
                    totalRead += read
                End While
                ' 4. 将字节数组转为 UTF-8 字符串
                Dim jsonMessage As String = Encoding.UTF8.GetString(bodyBytes)
                
                Console.WriteLine($"Received: {jsonMessage}")
                
                ' TODO: 在这里处理 jsonMessage，并准备回复
                ' ProcessMessage(jsonMessage, stream)
            End While
        End Using
    Catch ex As Exception
        Console.WriteLine($"Client error: {ex.Message}")
    End Try
End Sub
```
---
## 第三步：实现 LSP 消息的发送 (编码)
发送比读取简单得多。我们只需将 JSON 字符串转为字节数组，计算出长度，拼装 Header 发送即可。
在 `Module Program` 中添加一个辅助方法：
```vb
' 异步发送 LSP 消息
Private Async Function SendMessageAsync(stream As NetworkStream, jsonContent As String) As Task
    ' 1. 将 JSON 转为 UTF-8 字节
    Dim bodyBytes As Byte() = Encoding.UTF8.GetBytes(jsonContent)
    
    ' 2. 拼装 Header 字符串
    Dim header As String = $"Content-Length: {bodyBytes.Length}\r\n\r\n"
    Dim headerBytes As Byte() = Encoding.UTF8.GetBytes(header)
    
    ' 3. 先发 Header
    Await stream.WriteAsync(headerBytes, 0, headerBytes.Length)
    
    ' 4. 紧接着发 Body
    Await stream.WriteAsync(bodyBytes, 0, bodyBytes.Length)
    
    ' 5. 刷新流，确保立即发送出去
    Await stream.FlushAsync()
End Function
```
---
## 第四步：最小的闭环测试 (响应 Initialize)
为了证明我们的传输层可用，我们需要响应 LSP 的 `initialize` 请求。当 VS Code 连接时，它会首先发送这个请求。我们必须返回一个合法的 JSON 响应，否则 VS Code 会认为服务器启动失败。
在 `HandleClientAsync` 的 `TODO` 位置，替换为以下代码。这里我们使用 `System.Text.Json` (BCL 自带) 来简单解析和构造 JSON：
```vb
' 在文件顶部引入 JSON 命名空间
Imports System.Text.Json
Imports System.Text.Json.Nodes
' ... 在读取完 jsonMessage 之后：
Dim jsonNode As JsonNode = JsonNode.Parse(jsonMessage)
Dim method As String = If(jsonNode("method")?.ToString(), "")
' 检测是否是 initialize 请求
If method = "initialize" Then
    ' 从请求中提取 id，响应时必须带上相同的 id
    Dim requestId = jsonNode("id")
    
    ' 构造一个最简的初始化成功响应 JSON
    Dim responseJson As String = "{""jsonrpc"":""2.0"",""id"":" & requestId.ToString() & ",""result"":{""capabilities"":{""textDocumentSync"":1,""hoverProvider"":true,""completionProvider"":{""triggerCharacters"":["".""]}},""serverInfo"":{""name"":""PureBclLspServer"",""version"":""1.0.0""}}"
    
    ' 发送响应
    Await SendMessageAsync(stream, responseJson)
    Console.WriteLine("Sent: initialize response")
    
    ' 注意：LSP 规范要求在 initialize 响应之后，客户端会发 initialized 通知
    ' 此时服务器就算正式跑起来了
End If
```
---
## 如何在 VS Code 中测试
1.  **编译 .NET 项目**：确保你的 VB.NET 控制台程序编译成功。
2.  **启动服务器**：双击运行生成的 `.exe`，看到控制台打印 `LSP Server listening on port 8080...`。
3.  **配置 VS Code 扩展**：参考我们上一轮对话，编写一个最简单的 VS Code 扩展，让它的 `LanguageClient` 连接到 `127.0.0.1:8080`。
4.  **运行扩展**：按下 `F5` 启动扩展开发宿主窗口。
5.  **观察现象**：
    *   如果一切顺利，.NET 控制台会打印出收到了 `initialize` 请求，并打印出发送了响应。
    *   VS Code 底部状态栏不会报错，说明你的纯 BCL 实现的 TCP LSP 服务器已经成功与 VS Code 握手！
## 关键点总结
1.  **不依赖第三方库**：我们仅用了 `System.Net.Sockets` 处理 TCP，`System.Text` 处理字符编码，`System.Text.Json` 处理 JSON。完美符合 BCL 要求。
2.  **流式读取的严谨性**：核心在于**按字节读取 Header**，**按 Content-Length 精准读取 Body**。这是实现任何基于 Header-Length 协议（如 LSP, HTTP 1.1）的通用底层逻辑。
3.  **编码必须统一**：LSP 强制要求使用 UTF-8 编码。在 `Encoding.UTF8.GetBytes` 和 `Encoding.UTF8.GetString` 时必须确保一致，否则中文字符会导致 `Content-Length` 计算错误从而解析失败。
