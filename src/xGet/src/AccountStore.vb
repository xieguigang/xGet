Imports System.IO
Imports System.Text.Json

''' <summary>
''' a local store of the TOTP secrets returned by the server at registration
''' time, keyed by ``server|email``.
''' </summary>
''' <remarks>
''' the secrets are persisted as a json file under the user application data
''' directory, so no secret is ever hard coded into the program.
''' </remarks>
Public Class AccountStore

    Private Shared ReadOnly JsonOptions As New JsonSerializerOptions With {
        .WriteIndented = True,
        .PropertyNameCaseInsensitive = True
    }

    Private ReadOnly accounts As Dictionary(Of String, String)

    ''' <summary>
    ''' the physical path of the local accounts json file.
    ''' </summary>
    Public ReadOnly Property StoreFile As String

    Public Sub New()
        Dim folder As String = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "xGet")

        Call Directory.CreateDirectory(folder)

        Me.StoreFile = Path.Combine(folder, "accounts.json")
        Me.accounts = load()
    End Sub

    ''' <summary>
    ''' save (or update) the TOTP secret of the given server and email.
    ''' </summary>
    Public Sub Save(server As String, email As String, secret As String)
        accounts(accountKey(server, email)) = secret
        Call persist()
    End Sub

    ''' <summary>
    ''' look up the stored TOTP secret. when the email is empty and the server
    ''' has exactly one registered account, that account is returned.
    ''' </summary>
    ''' <param name="server">the server base url.</param>
    ''' <param name="email">the user email, may be empty.</param>
    ''' <returns>the stored secret, or <c>Nothing</c> when not found.</returns>
    Public Function GetSecret(server As String, email As String) As String
        If Not String.IsNullOrEmpty(email) Then
            Dim value As String = Nothing
            If accounts.TryGetValue(accountKey(server, email), value) Then
                Return value
            End If
            Return Nothing
        End If

        Dim prefix As String = normalizeServer(server) & "|"
        Dim matches As New List(Of String)

        For Each item In accounts
            If item.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) Then
                matches.Add(item.Value)
            End If
        Next

        If matches.Count = 1 Then
            Return matches(0)
        End If
        Return Nothing
    End Function

    Private Shared Function accountKey(server As String, email As String) As String
        Return normalizeServer(server) & "|" & email.Trim().ToLowerInvariant()
    End Function

    Private Shared Function normalizeServer(server As String) As String
        If server Is Nothing Then
            Return ""
        End If
        Return server.Trim().TrimEnd("/"c).ToLowerInvariant()
    End Function

    Private Function load() As Dictionary(Of String, String)
        If Not File.Exists(StoreFile) Then
            Return New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
        End If

        Try
            Dim json As String = File.ReadAllText(StoreFile)
            Dim data As Dictionary(Of String, String) = JsonSerializer.Deserialize(Of Dictionary(Of String, String))(json, JsonOptions)
            If data Is Nothing Then
                Return New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
            End If
            Return New Dictionary(Of String, String)(data, StringComparer.OrdinalIgnoreCase)
        Catch
            Return New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)
        End Try
    End Function

    Private Sub persist()
        Call File.WriteAllText(StoreFile, JsonSerializer.Serialize(accounts, JsonOptions))
    End Sub
End Class
