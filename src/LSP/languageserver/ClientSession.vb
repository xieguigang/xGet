Imports System.Collections.Generic
Imports System.Net.Sockets
Imports System.Text.Json.Nodes

''' <summary>
''' one LSP session per tcp client connection. it owns the network stream read /
''' write loop, maintains the open document set of this client, dispatches the
''' incoming json-rpc messages to the right provider and answers the requests.
''' </summary>
''' <remarks>
''' a session processes its messages sequentially on a single task, so the per
''' session <see cref="documents"/> dictionary is never accessed concurrently and
''' needs no locking. the shared <see cref="ApiIndex"/> is read only, so concurrent
''' sessions are safe as well.
''' </remarks>
Public Class ClientSession

    Private ReadOnly client As TcpClient
    Private ReadOnly index As ApiIndex
    Private ReadOnly documents As New Dictionary(Of String, String)(StringComparer.Ordinal)

    Private ReadOnly completion As CompletionProvider
    Private ReadOnly hover As HoverProvider
    Private ReadOnly signature As SignatureHelpProvider

    Public Sub New(client As TcpClient, index As ApiIndex)
        Me.client = client
        Me.index = index
        Me.completion = New CompletionProvider(index)
        Me.hover = New HoverProvider(index)
        Me.signature = New SignatureHelpProvider(index)
    End Sub

    ''' <summary>the per connection message pump. runs until the client disconnects.</summary>
    Public Async Function RunAsync() As Task
        Try
            Using stream As NetworkStream = client.GetStream()
                While client.Connected
                    Dim message As JsonObject = Await JsonRpc.ReadMessageAsync(stream)

                    If message Is Nothing Then
                        Exit While
                    End If

                    Await DispatchAsync(stream, message)
                End While
            End Using
        Catch ex As Exception
            ' a broken connection must not crash the whole server
            Console.WriteLine($"client error: {ex.Message}")
        Finally
            client.Dispose()
            Console.WriteLine("client disconnected")
        End Try
    End Function

    Private Async Function DispatchAsync(stream As NetworkStream, message As JsonObject) As Task
        Dim method As String = JsonRpc.Str(message, "method")
        Dim id As JsonNode = message("id")
        ' the id node belongs to the parsed request tree, so it already has a parent
        ' and can not be re-parented into the response; clone it first.
        Dim idClone As JsonNode = If(id Is Nothing, Nothing, id.DeepClone())
        Dim params As JsonNode = message("params")

        Select Case method
            Case "initialize"
                Await JsonRpc.SendMessageAsync(stream, MakeInitializeResult(idClone))

            Case "initialized"
                ' notification, nothing to answer

            Case "textDocument/didOpen"
                HandleDidOpen(params)

            Case "textDocument/didChange"
                HandleDidChange(params)

            Case "textDocument/didClose"
                HandleDidClose(params)

            Case "textDocument/completion"
                If idClone IsNot Nothing Then
                    Dim result As JsonNode = completion.Provide(params, documents)
                    Await JsonRpc.SendMessageAsync(stream, JsonRpc.MakeResponse(idClone, result))
                End If

            Case "textDocument/hover"
                If idClone IsNot Nothing Then
                    Dim result As JsonNode = hover.Provide(params, documents)
                    Await JsonRpc.SendMessageAsync(stream, JsonRpc.MakeResponse(idClone, result))
                End If

            Case "textDocument/signatureHelp"
                If idClone IsNot Nothing Then
                    Dim result As JsonNode = signature.Provide(params, documents)
                    Await JsonRpc.SendMessageAsync(stream, JsonRpc.MakeResponse(idClone, result))
                End If

            Case "shutdown"
                If idClone IsNot Nothing Then
                    Await JsonRpc.SendMessageAsync(stream, JsonRpc.MakeResponse(idClone, New JsonObject()))
                End If

            Case "exit"
                ' the client is leaving; close the connection
                client.Close()

            Case Else
                ' answer unknown requests with a method-not-found error so the
                ' client never blocks waiting for a response.
                If idClone IsNot Nothing Then
                    Await JsonRpc.SendMessageAsync(stream, JsonRpc.MakeError(idClone, -32601, "Method not found: " & method))
                End If
        End Select
    End Function

    Private Sub HandleDidOpen(params As JsonNode)
        If params Is Nothing Then
            Return
        End If

        Dim uri As String = JsonRpc.Str(params, "textDocument", "uri")
        Dim text As String = JsonRpc.Str(params, "textDocument", "text")

        If Not String.IsNullOrEmpty(uri) Then
            documents(uri) = text
        End If
    End Sub

    Private Sub HandleDidChange(params As JsonNode)
        If params Is Nothing Then
            Return
        End If

        Dim uri As String = JsonRpc.Str(params, "textDocument", "uri")
        Dim changes As JsonNode = params("contentChanges")

        If String.IsNullOrEmpty(uri) OrElse changes Is Nothing Then
            Return
        End If

        Dim array = TryCast(changes, JsonArray)

        If array IsNot Nothing AndAlso array.Count > 0 Then
            Dim textNode As JsonNode = array(0)("text")
            Dim newText As String = If(textNode Is Nothing, "", textNode.ToString())
            documents(uri) = newText
        End If
    End Sub

    Private Sub HandleDidClose(params As JsonNode)
        If params Is Nothing Then
            Return
        End If

        Dim uri As String = JsonRpc.Str(params, "textDocument", "uri")

        If Not String.IsNullOrEmpty(uri) Then
            documents.Remove(uri)
        End If
    End Sub

    ''' <summary>build the initialize result announcing the server capabilities.</summary>
    Private Function MakeInitializeResult(id As JsonNode) As JsonObject
        Dim capabilities As New JsonObject From {
            {"textDocumentSync", JsonValue.Create(1)},
            {"hoverProvider", JsonValue.Create(True)},
            {"completionProvider", New JsonObject From {
                {"triggerCharacters", New JsonArray(JsonValue.Create("."), JsonValue.Create(" "))},
                {"resolveProvider", JsonValue.Create(False)}
            }},
            {"signatureHelpProvider", New JsonObject From {
                {"triggerCharacters", New JsonArray(JsonValue.Create("("))}
            }}
        }

        Return New JsonObject From {
            {"jsonrpc", "2.0"},
            {"id", id},
            {"result", New JsonObject From {
                {"capabilities", capabilities},
                {"serverInfo", New JsonObject From {
                    {"name", JsonValue.Create("xDoc.VbScriptLsp")},
                    {"version", JsonValue.Create("1.0.0")}
                }}
            }}
        }
    End Function
End Class
