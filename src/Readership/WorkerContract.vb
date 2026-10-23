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

''' <summary>
''' one task of the api document daemon: the server writes one json line with
''' this payload into the stdin of the worker, and the worker answers with one
''' <see cref="ApiDocWorkerResponse"/> line on its stdout.
''' </summary>
Public Class ApiDocWorkerJob

    ''' <summary>the action which indexes (or re-indexes) one package version.</summary>
    Public Const ActionIndex As String = "index"

    ''' <summary>the action which rebuilds the static pages of one package version from the database.</summary>
    Public Const ActionRebuild As String = "rebuild"

    ''' <summary>the unique id of the job, echoed back by the worker.</summary>
    Public Property id As String

    ''' <summary>the job action, see <see cref="ActionIndex"/> / <see cref="ActionRebuild"/>.</summary>
    Public Property action As String

    ''' <summary>the package id.</summary>
    Public Property packageId As String

    ''' <summary>the package version.</summary>
    Public Property version As String

    ''' <summary>the physical path of the ``.nupkg`` file (an index job only).</summary>
    Public Property nupkg As String

    ''' <summary>the retry attempt counter, starting at 1.</summary>
    Public Property attempt As Integer
End Class

''' <summary>
''' the answer of the daemon for one <see cref="ApiDocWorkerJob"/>.
''' </summary>
Public Class ApiDocWorkerResponse

    ''' <summary>the id of the job which this response belongs to.</summary>
    Public Property id As String

    ''' <summary>whether the job was executed without a fatal error.</summary>
    Public Property ok As Boolean

    ''' <summary>the fatal error message, or a short status text on success.</summary>
    Public Property message As String

    ''' <summary>the non fatal warnings collected while the job was executed.</summary>
    Public Property warnings As New List(Of String)

    ''' <summary>the number of the indexed types.</summary>
    Public Property types As Integer

    ''' <summary>the number of the static html pages which were written.</summary>
    Public Property pages As Integer

    ''' <summary>whether the document rows were written into the database.</summary>
    Public Property documents As Boolean

    ''' <summary>the wall clock time of the job in milliseconds.</summary>
    Public Property elapsedMs As Long
End Class
