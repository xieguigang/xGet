Imports System.Collections.Generic
Imports System.IO
Imports System.Net.Http
Imports System.Net.Http.Headers
Imports System.Text.Json
Imports System.Threading

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
End Class

''' <summary>
''' a small http client for the experimental nuget server: register a new email
''' (which returns the TOTP secret) and upload a nupkg with a TOTP code.
''' </summary>
''' <remarks>
''' downloads are intentionally not implemented here, the official visual
''' studio nuget client is used to anonymously download packages.
''' </remarks>
Public Class NugetApiClient

    Private Shared ReadOnly JsonOptions As New JsonSerializerOptions With {
        .PropertyNameCaseInsensitive = True
    }

    ' the built-in 100 seconds timeout is intentionally disabled: every request
    ' carries its own cancellation token, so that a longer "--timeout" value is
    ' not silently capped by the default (the effective timeout is the earlier
    ' of the two).
    Private Shared ReadOnly Http As New HttpClient() With {
        .Timeout = Timeout.InfiniteTimeSpan
    }

    ''' <summary>
    ''' the timeout applied when the caller does not pass one explicitly.
    ''' </summary>
    Public Shared ReadOnly DefaultTimeout As TimeSpan = TimeSpan.FromMinutes(15)

    Private ReadOnly baseUrl As String

    Public Sub New(server As String)
        Me.baseUrl = normalize(server)
    End Sub

    ''' <summary>
    ''' register the given email on the server and return the TOTP secret.
    ''' </summary>
    Public Function Register(email As String, Optional timeout As TimeSpan? = Nothing) As ApiResult
        Using content As New FormUrlEncodedContent(New Dictionary(Of String, String) From {
            {"email", email}
        })
            Using cts As CancellationTokenSource = createTimeout(timeout)
                Try
                    Using response As HttpResponseMessage = Http _
                        .PostAsync($"{baseUrl}/api/register", content, cts.Token) _
                        .GetAwaiter() _
                        .GetResult()

                        Return parse(response)
                    End Using
                Catch ex As OperationCanceledException
                    Return timeoutResult(timeout)
                End Try
            End Using
        End Using
    End Function

    ''' <summary>
    ''' upload a nupkg file with the given email and TOTP code through the
    ''' custom multipart endpoint.
    ''' </summary>
    Public Function Upload(email As String, code As String, nupkgPath As String, Optional timeout As TimeSpan? = Nothing) As ApiResult
        Using form As New MultipartFormDataContent()
            Call form.Add(New StringContent(email), "email")
            Call form.Add(New StringContent(code), "code")

            Dim bytes As Byte() = File.ReadAllBytes(nupkgPath)
            Dim fileContent As New ByteArrayContent(bytes)
            fileContent.Headers.ContentType = New MediaTypeHeaderValue("application/octet-stream")
            Call form.Add(fileContent, "file", Path.GetFileName(nupkgPath))

            Using cts As CancellationTokenSource = createTimeout(timeout)
                Try
                    Using response As HttpResponseMessage = Http _
                        .PostAsync($"{baseUrl}/api/v2/package", form, cts.Token) _
                        .GetAwaiter() _
                        .GetResult()

                        Return parse(response)
                    End Using
                Catch ex As OperationCanceledException
                    Return timeoutResult(timeout)
                End Try
            End Using
        End Using
    End Function

    ''' <summary>
    ''' create the cancellation token source that enforces the request timeout,
    ''' falling back to <see cref="DefaultTimeout"/> when none is given.
    ''' </summary>
    Private Shared Function createTimeout(timeout As TimeSpan?) As CancellationTokenSource
        Return New CancellationTokenSource(If(timeout, DefaultTimeout))
    End Function

    ''' <summary>
    ''' build the failure result reported when a request is cancelled by its
    ''' timeout token.
    ''' </summary>
    Private Shared Function timeoutResult(timeout As TimeSpan?) As ApiResult
        Dim effective As TimeSpan = If(timeout, DefaultTimeout)

        Return New ApiResult With {
            .ok = False,
            .message = $"request timed out after {describeTimeout(effective)}"
        }
    End Function

    Private Shared Function describeTimeout(value As TimeSpan) As String
        If value.TotalMinutes >= 1 Then
            Return $"{value.TotalMinutes:0.##} minute(s)"
        End If

        Return $"{value.TotalSeconds:0.##} second(s)"
    End Function

    Private Shared Function parse(response As HttpResponseMessage) As ApiResult
        Dim text As String = response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        Dim result As ApiResult = Nothing

        Try
            result = JsonSerializer.Deserialize(Of ApiResult)(text, JsonOptions)
        Catch
            result = Nothing
        End Try

        If result Is Nothing Then
            result = New ApiResult With {
                .ok = False,
                .message = text
            }
        End If

        If Not response.IsSuccessStatusCode Then
            result.ok = False
            If String.IsNullOrEmpty(result.message) Then
                result.message = $"{CInt(response.StatusCode)} {response.ReasonPhrase}"
            End If
        End If

        Return result
    End Function

    Private Shared Function normalize(server As String) As String
        Dim url As String = If(server, "").Trim().TrimEnd("/"c)

        If url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) OrElse
           url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) Then
            Return url
        End If

        Return "http://" & url
    End Function
End Class
