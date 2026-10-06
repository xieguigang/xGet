''' <summary>
''' the json contract between the nuget server and the <c>ApiDocWorker</c>
''' child process: the worker performs the untrusted api comment document
''' extraction of one uploaded package in its own process and reports the
''' extracted document model back through this payload.
''' </summary>
Public Class ApiDocWorkerResult

    ''' <summary>whether the extraction finished without a fatal error.</summary>
    Public Property ok As Boolean

    ''' <summary>the fatal error message (empty on success).</summary>
    Public Property message As String

    ''' <summary>the non fatal warnings collected during the extraction.</summary>
    Public Property warnings As New List(Of String)

    ''' <summary>the extracted document model (<c>Nothing</c> when not available).</summary>
    Public Property document As ApiDocDocument
End Class
