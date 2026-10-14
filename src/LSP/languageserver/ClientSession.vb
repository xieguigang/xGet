Imports System.Net.Sockets
Imports Microsoft.VisualBasic.MIME.application.json
Imports Microsoft.VisualBasic.MIME.application.json.Javascript

''' <summary>
''' one LSP session per tcp client connection. it owns the open document set of
''' this client, dispatches the incoming json-rpc messages to the right provider
''' and answers the requests. the generic message pump lives in the
''' <see cref="JsonRpcSession"/> base class.
''' </summary>
''' <remarks>
''' a session processes its messages sequentially on a single task, so the per
''' session <see cref="documents"/> dictionary is never accessed concurrently and
''' needs no locking. the shared <see cref="ApiIndex"/> is read only, so concurrent
''' sessions are safe as well.
''' </remarks>
Public Class ClientSession : Inherits JsonRpcSession

    Private ReadOnly index As ApiIndex
    Private ReadOnly documents As New Dictionary(Of String, String)(StringComparer.Ordinal)

    Private ReadOnly completion As CompletionProvider
    Private ReadOnly hover As HoverProvider
    Private ReadOnly signature As SignatureHelpProvider

    Public Sub New(client As TcpClient, index As ApiIndex)
        MyBase.New(client)
        Me.index = index
        Me.completion = New CompletionProvider(index)
        Me.hover = New HoverProvider(index)
        Me.signature = New SignatureHelpProvider(index)
    End Sub

    ''' <summary>route a json-rpc request (carries an id) to the matching LSP handler.</summary>
    Protected Overrides Async Function HandleRequestAsync(stream As NetworkStream, method As String, id As JsonElement, params As JsonElement) As Task
        Select Case method
            Case "initialize"
                Await ReplyAsync(stream, id, MakeInitializeResult(id))

            Case "textDocument/completion"
                Await ReplyAsync(stream, id, completion.Provide(params, documents))

            Case "textDocument/hover"
                Await ReplyAsync(stream, id, hover.Provide(params, documents))

            Case "textDocument/signatureHelp"
                Await ReplyAsync(stream, id, signature.Provide(params, documents))

            Case "shutdown"
                Await ReplyAsync(stream, id, New JsonObject())

            Case Else
                ' answer unknown requests with a method-not-found error so the
                ' client never blocks waiting for a response.
                Await ReplyErrorAsync(stream, id, -32601, "Method not found: " & method)
        End Select
    End Function

    ''' <summary>route a json-rpc notification (no id) to the matching LSP handler.</summary>
    Protected Overrides Function HandleNotificationAsync(stream As NetworkStream, method As String, params As JsonElement) As Task
        Select Case method
            Case "initialized"
                ' notification, nothing to answer

            Case "textDocument/didOpen"
                HandleDidOpen(params)

            Case "textDocument/didChange"
                HandleDidChange(params)

            Case "textDocument/didClose"
                HandleDidClose(params)

            Case "exit"
                ' the client is leaving; close the connection
                client.Close()
        End Select

        Return Task.CompletedTask
    End Function

    Private Sub HandleDidOpen(params As JsonElement)
        If params Is Nothing Then
            Return
        End If

        Dim uri As String = JsonRpc.Str(params, "textDocument", "uri")
        Dim text As String = JsonRpc.Str(params, "textDocument", "text")

        If Not String.IsNullOrEmpty(uri) Then
            documents(uri) = text
        End If
    End Sub

    Private Sub HandleDidChange(params As JsonObject)
        If params Is Nothing Then
            Return
        End If

        Dim uri As String = JsonRpc.Str(params, "textDocument", "uri")
        Dim changes As JsonElement = params("contentChanges")

        If String.IsNullOrEmpty(uri) OrElse changes Is Nothing Then
            Return
        End If

        Dim array = TryCast(changes, JsonArray)

        If array IsNot Nothing AndAlso array.Count > 0 Then
            Dim textNode As JsonValue = DirectCast(array(0), JsonObject)("text")
            Dim newText As String = If(textNode Is Nothing, "", textNode.ToString())
            documents(uri) = newText
        End If
    End Sub

    Private Sub HandleDidClose(params As JsonElement)
        If params Is Nothing Then
            Return
        End If

        Dim uri As String = JsonRpc.Str(params, "textDocument", "uri")

        If Not String.IsNullOrEmpty(uri) Then
            documents.Remove(uri)
        End If
    End Sub

    ''' <summary>build the initialize result announcing the server capabilities.</summary>
    Private Function MakeInitializeResult(id As JsonElement) As JsonObject
        Dim capabilities As New JsonObject From {
            {"textDocumentSync", New JsonValue(1)},
            {"hoverProvider", New JsonValue(True)},
            {"completionProvider", New JsonObject From {
                {"triggerCharacters", New JsonArray(New JsonValue("."), New JsonValue(" "))},
                {"resolveProvider", New JsonValue(False)}
            }},
            {"signatureHelpProvider", New JsonObject From {
                {"triggerCharacters", New JsonArray(New JsonValue("("))}
            }}
        }

        Return New JsonObject From {
            {"jsonrpc", "2.0"},
            {"id", id},
            {"result", New JsonObject From {
                {"capabilities", capabilities},
                {"serverInfo", New JsonObject From {
                    {"name", New JsonValue("xDoc.VbScriptLsp")},
                    {"version", New JsonValue("1.0.0")}
                }}
            }}
        }
    End Function
End Class
