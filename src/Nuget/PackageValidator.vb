Imports System.IO
Imports System.IO.Compression

''' <summary>
''' the content validator of the uploaded nuget packages: the feed only accepts
''' library packages which ship at least one managed .NET clr dll inside their
''' ``lib`` folder. A package which carries any executable image (``.exe``) or
''' which ships no managed clr assembly at all is rejected before any file is
''' written to its final location and before any database row is created.
''' 
''' the ``managed`` test reads the pe header of the candidate dll only: a dll
''' counts as a managed assembly when its optional header declares a non zero
''' clr runtime header data directory (data directory index 14). the check is
''' purely structural, no assembly is loaded and no code is executed.
''' </summary>
Public Module PackageValidator

    ''' <summary>
    ''' validate the ``lib`` content of one uploaded nupkg.
    ''' </summary>
    ''' <param name="nupkgPath">the physical nupkg path.</param>
    ''' <param name="rejectReason">the human readable reject reason (empty when accepted).</param>
    ''' <returns><c>True</c> when the package content is acceptable.</returns>
    Public Function Validate(nupkgPath As String, ByRef rejectReason As String) As Boolean
        rejectReason = ""

        If String.IsNullOrEmpty(nupkgPath) OrElse Not File.Exists(nupkgPath) Then
            rejectReason = "the package file was not found."
            Return False
        End If

        Using zip As ZipArchive = ZipFile.OpenRead(nupkgPath)
            Dim managedDll As Boolean = False

            For Each entry As ZipArchiveEntry In zip.Entries
                Dim name As String = entry.FullName.Replace("\"c, "/"c)

                If Not name.StartsWith("lib/", StringComparison.OrdinalIgnoreCase) Then
                    Continue For
                End If

                Dim extension As String = Path.GetExtension(name).ToLowerInvariant()

                ' rule 1: executable images are never accepted
                If extension = ".exe" Then
                    rejectReason = $"the package ships an executable image in its lib folder ('{name}'): " &
                                   "only library packages with managed .net clr dll assemblies are accepted."
                    Return False
                End If

                ' rule 2: at least one managed clr dll is required
                If extension = ".dll" AndAlso Not managedDll Then
                    Try
                        Using stream As Stream = entry.Open()
                            If IsManagedAssembly(stream) Then
                                managedDll = True
                            End If
                        End Using
                    Catch
                        ' a truncated or fake dll simply does not count
                    End Try
                End If
            Next

            If Not managedDll Then
                rejectReason = "the package ships no managed .net clr dll in its lib folder: " &
                               "only library packages with managed .net clr dll assemblies are accepted."
                Return False
            End If
        End Using

        Return True
    End Function

    ''' <summary>
    ''' structural test whether the given pe image is a managed assembly: the
    ''' clr runtime header entry (data directory index 14) of the optional
    ''' header must be non zero.
    ''' </summary>
    ''' <param name="stream">the positioned pe image stream (read from any offset).</param>
    Public Function IsManagedAssembly(stream As Stream) As Boolean
        If stream Is Nothing OrElse Not stream.CanRead OrElse Not stream.CanSeek Then
            Return False
        End If

        Try
            If stream.Length < &H40 Then
                Return False
            End If

            ' dos header: "MZ" (little endian 0x4D 'M', 0x5A 'Z')
            Call stream.Seek(0, SeekOrigin.Begin)
            If readUInt16(stream) <> &H5A4DI Then
                Return False
            End If

            ' the pe header offset from the dos stub
            Call stream.Seek(&H3C, SeekOrigin.Begin)
            Dim peOffset As Integer = readInt32(stream)

            If peOffset <= 0 OrElse peOffset + 264 > stream.Length Then
                Return False
            End If

            ' the "PE\0\0" signature
            Call stream.Seek(peOffset, SeekOrigin.Begin)
            If readInt32(stream) <> &H4550I Then
                Return False
            End If

            ' the optional header follows the 20 bytes coff header
            Dim optionalHeader As Long = peOffset + 24
            Call stream.Seek(optionalHeader, SeekOrigin.Begin)

            Dim magic As Integer = readUInt16(stream)
            Dim dataDirectories As Long

            Select Case magic
                Case &H10BI  ' pe32
                    dataDirectories = optionalHeader + 96
                Case &H20BI  ' pe32+
                    dataDirectories = optionalHeader + 112
                Case Else
                    Return False
            End Select

            ' data directory index 14: the clr runtime header
            Call stream.Seek(dataDirectories + 14 * 8, SeekOrigin.Begin)
            Dim clrRva As Integer = readInt32(stream)

            Return clrRva <> 0
        Catch
            Return False
        End Try
    End Function

    Private Function readUInt16(stream As Stream) As Integer
        Dim low As Integer = stream.ReadByte()
        Dim high As Integer = stream.ReadByte()

        If high < 0 Then
            Throw New EndOfStreamException()
        End If

        Return low Or (high << 8)
    End Function

    Private Function readInt32(stream As Stream) As Integer
        Dim b0 As Integer = stream.ReadByte()
        Dim b1 As Integer = stream.ReadByte()
        Dim b2 As Integer = stream.ReadByte()
        Dim b3 As Integer = stream.ReadByte()

        If b3 < 0 Then
            Throw New EndOfStreamException()
        End If

        Return b0 Or (b1 << 8) Or (b2 << 16) Or (b3 << 24)
    End Function
End Module
