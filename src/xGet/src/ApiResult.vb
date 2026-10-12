
''' <summary>
''' the parsed json response of the experimental nuget server api.
''' </summary>
Public Class ApiResult
    Public Property ok As Boolean
    Public Property message As String
    Public Property email As String
    Public Property secret As String
    Public Property id As String
    Public Property version As String

    ''' <summary>
    ''' an optional machine readable warning code of the response, for example
    ''' ``mail-not-configured`` when the server can not send the verification
    ''' email because no smtp account was configured yet.
    ''' </summary>
    Public Property warning As String
End Class