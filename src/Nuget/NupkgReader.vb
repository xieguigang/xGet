Imports System.Collections.Generic
Imports System.IO
Imports System.IO.Compression
Imports System.Linq
Imports System.Text
Imports System.Xml
Imports System.Xml.Linq

''' <summary>
''' the extraction limits of one nupkg zip container. every limit is enforced
''' against the actual streamed byte count (and never against the untrusted
''' ``entry.Length`` field of the zip central directory), so a decompression
''' bomb can not exhaust the server disk or memory.
''' </summary>
Public Class ZipExtractionLimits

    ''' <summary>the maximum uncompressed size of one zip entry.</summary>
    Public Property MaxEntryBytes As Long

    ''' <summary>the maximum total uncompressed size of all entries.</summary>
    Public Property MaxTotalBytes As Long

    ''' <summary>the maximum number of entries of the container.</summary>
    Public Property MaxEntries As Integer

    Public Sub New(Optional maxEntryMB As Double = 64,
                   Optional maxTotalMB As Double = 512,
                   Optional maxEntries As Integer = 2048)
        Me.MaxEntryBytes = CLng(maxEntryMB * 1024 * 1024)
        Me.MaxTotalBytes = CLng(maxTotalMB * 1024 * 1024)
        Me.MaxEntries = maxEntries
    End Sub
End Class

''' <summary>
''' one dependency entry of the nuspec manifest.
''' </summary>
Public Class NuspecDependency
    Public Property targetFramework As String
    Public Property id As String
    Public Property range As String
End Class

''' <summary>
''' the full package metadata parsed from the ``.nuspec`` manifest.
''' </summary>
Public Class NupkgMetadata
    Public Property Id As String
    Public Property Version As String
    Public Property Title As String
    Public Property Authors As String
    Public Property Owners As String
    Public Property Description As String
    Public Property Summary As String
    Public Property ReleaseNotes As String
    Public Property Copyright As String
    Public Property Language As String
    Public Property Tags As String
    Public Property ProjectUrl As String
    Public Property LicenseUrl As String
    Public Property License As String
    Public Property RequireLicenseAcceptance As String
    Public Property Repository As String
    Public Property Icon As String

    ''' <summary>
    ''' the package relative path of the readme document declared by the nuspec
    ''' ``&lt;readme&gt;`` element, for example ``README.md`` or ``docs/README.md``.
    ''' </summary>
    Public Property Readme As String

    ''' <summary>the dependency list encoded as ``id|range`` pairs.</summary>
    Public Property Dependencies As String

    ''' <summary>the structured dependency list grouped by target framework.</summary>
    Public Property DependencyItems As New List(Of NuspecDependency)

    ''' <summary>the raw nuspec xml document.</summary>
    Public Property RawXml As String
End Class

''' <summary>
''' a minimal nupkg reader: open the OPC zip container, extract the ``.nuspec``
''' manifest and parse the package metadata. the binary itself is never stored
''' inside the database.
''' </summary>
Public Module NupkgReader

    ''' <summary>
    ''' the safe xml reader settings: DTD processing is prohibited (which rules
    ''' out both the XXE entity expansion and the billion laughs attack), no
    ''' external resolver is installed and the entity expansion is capped.
    ''' </summary>
    Private ReadOnly SafeXmlSettings As New XmlReaderSettings With {
        .DtdProcessing = DtdProcessing.Prohibit,
        .XmlResolver = Nothing,
        .MaxCharactersFromEntities = 0,
        .CloseInput = True
    }

    ''' <summary>
    ''' the maximum character count of one parsed text entry (the nuspec, the
    ''' readme documents and so on).
    ''' </summary>
    Private Const MaxTextEntryChars As Integer = 4 * 1024 * 1024

    Public Function ReadMetadata(nupkgPath As String) As NupkgMetadata
        Dim raw As String = ReadNuspecXml(nupkgPath)
        Dim document As XDocument = parseSafeXml(raw)
        Dim metadata As NupkgMetadata = parse(document)
        metadata.RawXml = raw
        Return metadata
    End Function

    ''' <summary>
    ''' parse an xml text through the safe reader settings: a document which
    ''' carries a DTD is rejected outright.
    ''' </summary>
    Private Function parseSafeXml(raw As String) As XDocument
        Using reader As XmlReader = XmlReader.Create(New StringReader(raw), SafeXmlSettings)
            Return XDocument.Load(reader)
        End Using
    End Function

    Public Function ReadNuspecXml(nupkgPath As String) As String
        Using zip As ZipArchive = ZipFile.OpenRead(nupkgPath)
            Dim entry As ZipArchiveEntry = findNuspec(zip)
            If entry Is Nothing Then
                Throw New InvalidDataException("the nuspec manifest was not found in the package.")
            End If
            Using stream As Stream = entry.Open()
                Return ReadTextEntry(stream)
            End Using
        End Using
    End Function

    ''' <summary>
    ''' read a text entry with a hard character budget: a nuspec which claims a
    ''' tiny size but expands to gigabytes of text can not exhaust the memory.
    ''' </summary>
    Private Function ReadTextEntry(stream As Stream) As String
        Using reader As New StreamReader(stream, detectEncodingFromByteOrderMarks:=True)
            Dim buffer(16 * 1024 - 1) As Char
            Dim sb As New StringBuilder
            Dim total As Integer = 0
            Dim read As Integer

            Do
                read = reader.Read(buffer, 0, buffer.Length)

                If read <= 0 Then
                    Exit Do
                End If

                total += read

                If total > MaxTextEntryChars Then
                    Throw New InvalidDataException($"the xml entry exceeds the {MaxTextEntryChars \ (1024 * 1024)} MB text limit.")
                End If

                Call sb.Append(buffer, 0, read)
            Loop

            Return sb.ToString()
        End Using
    End Function

    ''' <summary>
    ''' the whitelist of the icon image extensions which may be extracted from a
    ''' package.
    ''' </summary>
    Public ReadOnly IconExtensions As String() = {".png", ".jpg", ".jpeg", ".gif", ".ico", ".svg", ".webp"}

    ''' <summary>
    ''' the whitelist of the readme document extensions which may be extracted
    ''' from a package.
    ''' </summary>
    Public ReadOnly ReadmeExtensions As String() = {".md", ".markdown", ".txt"}

    ''' <summary>
    ''' resolve the file extension of a nuspec declared icon / readme entry
    ''' against a whitelist. Every other extension (or a name which carries no
    ''' extension at all) falls back to the given default, so that a hostile
    ''' value like ``../../evil.exe`` can never control the file name of the
    ''' extraction target.
    ''' </summary>
    Public Function SafeExtension(name As String, allowed As String(), fallback As String) As String
        Dim extension As String = Path.GetExtension(If(name, ""))

        If extension.StringEmpty() Then
            Return fallback
        End If

        extension = extension.ToLowerInvariant()

        For Each item As String In allowed
            If item.Equals(extension, StringComparison.Ordinal) Then
                Return extension
            End If
        Next

        Return fallback
    End Function

    ''' <summary>
    ''' extract the embedded icon image (if any) to the given destination file.
    ''' </summary>
    ''' <param name="nupkgPath">the physical nupkg path.</param>
    ''' <param name="iconName">the icon path stored in the nuspec ``icon`` element.</param>
    ''' <param name="destination">the file path to write the icon bytes to.</param>
    ''' <param name="limits">the extraction limits (optional).</param>
    ''' <returns><c>True</c> when an icon image was extracted.</returns>
    Public Function ExtractIcon(nupkgPath As String, iconName As String, destination As String,
                                Optional limits As ZipExtractionLimits = Nothing) As Boolean
        If String.IsNullOrEmpty(iconName) Then
            Return False
        End If

        Using zip As ZipArchive = ZipFile.OpenRead(nupkgPath)
            Dim key As String = iconName.Replace("\"c, "/"c).TrimStart("/"c)
            Dim entry As ZipArchiveEntry = zip.Entries _
                .FirstOrDefault(Function(e) e.FullName.Equals(key, StringComparison.OrdinalIgnoreCase))

            If entry Is Nothing OrElse entry.Length = 0 Then
                Return False
            End If

            Dim folder As String = Path.GetDirectoryName(destination)
            If Not String.IsNullOrEmpty(folder) Then
                Call Directory.CreateDirectory(folder)
            End If

            Call assertDestination(destination)

            Using source As Stream = entry.Open()
                Using target As Stream = File.Create(destination)
                    Call copyLimited(source, target, limits, 0)
                End Using
            End Using

            Return True
        End Using
    End Function

    ''' <summary>
    ''' extract a text entry of the package (for example the readme markdown
    ''' document) to the given destination file, normalizing the encoding to
    ''' utf-8 without a byte order mark.
    ''' </summary>
    ''' <param name="nupkgPath">the physical nupkg path.</param>
    ''' <param name="entryName">
    ''' the package relative entry path as declared in the nuspec, for example
    ''' ``README.md`` or ``docs/README.md``.
    ''' </param>
    ''' <param name="destination">the file path to write the entry text to.</param>
    ''' <param name="limits">the extraction limits (optional).</param>
    ''' <returns><c>True</c> when the entry was found and extracted.</returns>
    Public Function ExtractEntry(nupkgPath As String, entryName As String, destination As String,
                                 Optional limits As ZipExtractionLimits = Nothing) As Boolean
        If String.IsNullOrEmpty(entryName) Then
            Return False
        End If

        Using zip As ZipArchive = ZipFile.OpenRead(nupkgPath)
            Dim key As String = entryName.Replace("\"c, "/"c).TrimStart("/"c)
            Dim entry As ZipArchiveEntry = findEntry(zip, key)

            If entry Is Nothing OrElse entry.Length = 0 Then
                Return False
            End If

            Dim folder As String = Path.GetDirectoryName(destination)
            If Not String.IsNullOrEmpty(folder) Then
                Call Directory.CreateDirectory(folder)
            End If

            Call assertDestination(destination)

            Dim text As String

            Using stream As Stream = entry.Open()
                text = ReadTextEntry(stream)
            End Using

            Call File.WriteAllText(destination, text, New UTF8Encoding(encoderShouldEmitUTF8Identifier:=False))
            Return True
        End Using
    End Function

    ''' <summary>
    ''' extract the xml comment documents (and the sibling clr assemblies which
    ''' are used by the reflection supplement) of the ``lib`` folder into the
    ''' given destination folder.
    ''' 
    ''' a package may ship several target frameworks; the target framework folder
    ''' with the highest priority which actually contains an xml comment document
    ''' is used, and the entries are flattened into the destination folder so
    ''' that every ``*.xml`` finds its sibling ``*.dll`` by the file name.
    ''' 
    ''' every limit of <paramref name="limits"/> is enforced against the actual
    ''' streamed byte count, so a decompression bomb can not exhaust the disk.
    ''' </summary>
    ''' <param name="nupkgPath">the physical nupkg path.</param>
    ''' <param name="destination">the folder to extract the comment documents to.</param>
    ''' <param name="limits">the extraction limits (optional).</param>
    ''' <returns>the number of the extracted xml comment documents.</returns>
    Public Function ExtractLibComments(nupkgPath As String, destination As String,
                                       Optional limits As ZipExtractionLimits = Nothing) As Integer
        If limits Is Nothing Then
            limits = New ZipExtractionLimits()
        End If

        Using zip As ZipArchive = ZipFile.OpenRead(nupkgPath)
            If zip.Entries.Count > limits.MaxEntries Then
                Throw New InvalidDataException($"the package carries {zip.Entries.Count} entries which exceeds the limit of {limits.MaxEntries}.")
            End If

            Dim groups As New Dictionary(Of String, List(Of ZipArchiveEntry))(StringComparer.OrdinalIgnoreCase)

            For Each entry As ZipArchiveEntry In zip.Entries
                Dim name As String = entry.FullName.Replace("\"c, "/"c)

                If Not name.StartsWith("lib/", StringComparison.OrdinalIgnoreCase) Then
                    Continue For
                End If

                If entry.Length = 0 Then
                    Continue For
                End If

                Dim extension As String = Path.GetExtension(name).ToLowerInvariant()

                If extension <> ".xml" AndAlso extension <> ".dll" Then
                    Continue For
                End If

                Dim folder As String = getDirectoryName(name)
                Dim list As List(Of ZipArchiveEntry) = Nothing

                If Not groups.TryGetValue(folder, list) Then
                    list = New List(Of ZipArchiveEntry)
                    groups(folder) = list
                End If

                Call list.Add(entry)
            Next

            ' pick the highest priority framework folder which ships a comment document
            Dim best As List(Of ZipArchiveEntry) = Nothing
            Dim bestRank As Integer = Integer.MinValue

            For Each item In groups
                Dim hasXml As Boolean = item.Value.Any(Function(e) Path.GetExtension(e.FullName).Equals(".xml", StringComparison.OrdinalIgnoreCase))

                If Not hasXml Then
                    Continue For
                End If

                Dim rank As Integer = TfmRank(tfmOf(item.Key))

                If best Is Nothing OrElse rank > bestRank Then
                    best = item.Value
                    bestRank = rank
                End If
            Next

            If best Is Nothing Then
                Return 0
            End If

            Dim destinationFull As String = Path.GetFullPath(Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination)))
            Call Directory.CreateDirectory(destinationFull)

            Dim count As Integer = 0
            Dim totalBytes As Long = 0

            For Each entry As ZipArchiveEntry In best
                Dim fileName As String = Path.GetFileName(entry.FullName.Replace("\"c, "/"c))

                ' skip the dot entries which ``Path.GetFileName`` passes through
                If fileName.StringEmpty() OrElse fileName = "." OrElse fileName = ".." Then
                    Continue For
                End If

                Dim target As String = Path.Combine(destinationFull, fileName)
                Dim targetFull As String = Path.GetFullPath(target)

                ' the extraction target must stay inside the destination folder
                If Not targetFull.StartsWith(destinationFull & Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) Then
                    Continue For
                End If

                Try
                    Using source As Stream = entry.Open()
                        Using targetStream As Stream = File.Create(targetFull)
                            Call copyLimited(source, targetStream, limits, totalBytes)
                        End Using
                    End Using

                    If Path.GetExtension(targetFull).Equals(".xml", StringComparison.OrdinalIgnoreCase) Then
                        count += 1
                    End If
                Catch ex As Exception
                    ' a single unreadable entry should not break the whole extraction
                End Try
            Next

            Return count
        End Using
    End Function

    ''' <summary>
    ''' stream copy one entry with the extraction limits enforced against the
    ''' actual byte count.
    ''' </summary>
    ''' <param name="source">the (deflated) entry stream.</param>
    ''' <param name="target">the destination file stream.</param>
    ''' <param name="limits">the limits of the current extraction.</param>
    ''' <param name="totalBytes">the running total byte counter of the extraction.</param>
    Private Sub copyLimited(source As Stream, target As Stream, limits As ZipExtractionLimits, ByRef totalBytes As Long)
        If limits Is Nothing Then
            limits = New ZipExtractionLimits()
        End If

        Dim buffer(8191) As Byte
        Dim entryBytes As Long = 0
        Dim read As Integer

        Do
            read = source.Read(buffer, 0, buffer.Length)

            If read <= 0 Then
                Exit Do
            End If

            entryBytes += read
            totalBytes += read

            If entryBytes > limits.MaxEntryBytes Then
                Throw New InvalidDataException($"one zip entry exceeds the {limits.MaxEntryBytes \ (1024 * 1024)} MB size limit.")
            End If

            If totalBytes > limits.MaxTotalBytes Then
                Throw New InvalidDataException($"the zip entries exceed the {limits.MaxTotalBytes \ (1024 * 1024)} MB total size limit.")
            End If

            Call target.Write(buffer, 0, read)
        Loop
    End Sub

    ''' <summary>
    ''' test whether the given destination file path is well formed and does not
    ''' escape its declared directory.
    ''' </summary>
    Private Sub assertDestination(destination As String)
        Dim full As String = Path.GetFullPath(destination)
        Dim fileName As String = Path.GetFileName(full)

        If fileName.StringEmpty() OrElse fileName = "." OrElse fileName = ".." Then
            Throw New InvalidDataException("the extraction destination file name is invalid.")
        End If
    End Sub

    ''' <summary>
    ''' the directory part of a package relative entry path
    ''' </summary>
    Private Function getDirectoryName(name As String) As String
        Dim index As Integer = name.LastIndexOf("/"c)

        If index <= 0 Then
            Return "lib"
        End If

        Return name.Substring(0, index)
    End Function

    ''' <summary>
    ''' the target framework moniker of a ``lib/&lt;tfm&gt;`` folder
    ''' </summary>
    Private Function tfmOf(folder As String) As String
        Dim parts = folder.Split("/"c)

        If parts.Length >= 2 Then
            Return parts(1)
        End If

        Return ""
    End Function

    ''' <summary>
    ''' the priority of a target framework moniker; a higher value wins.
    ''' ``net10.0`` &gt; ``netstandard2.0`` &gt; ``net48``.
    ''' </summary>
    Private Function TfmRank(tfm As String) As Integer
        If String.IsNullOrWhiteSpace(tfm) Then
            Return 0
        End If

        Dim text As String = tfm.Trim().ToLowerInvariant()

        If text.StartsWith("netstandard", StringComparison.Ordinal) Then
            Return 5000 + versionRank(text.Substring("netstandard".Length))
        End If

        If text.StartsWith("netcoreapp", StringComparison.Ordinal) Then
            Return 7000 + versionRank(text.Substring("netcoreapp".Length))
        End If

        If text.StartsWith("net", StringComparison.Ordinal) Then
            Dim rest As String = text.Substring("net".Length)

            ' a dotted moniker (net10.0) is a modern .net, a two/three digit
            ' moniker (net48) is the legacy .net framework.
            If rest.Contains(".") OrElse rest.Length = 0 Then
                Return 10000 + versionRank(rest)
            End If

            If rest.Length <= 3 Then
                Return 100 + versionRank(rest)
            End If

            Return 10000 + versionRank(rest)
        End If

        Return 1000
    End Function

    Private Function versionRank(version As String) As Integer
        Dim parts = If(version, "").Split("."c)
        Dim major As Integer = 0
        Dim minor As Integer = 0

        If parts.Length > 0 Then
            Integer.TryParse(parts(0), major)
        End If

        If parts.Length > 1 Then
            Integer.TryParse(parts(1), minor)
        End If

        Return major * 100 + minor
    End Function

    ''' <summary>
    ''' locate an entry by its package relative path, tolerating the path
    ''' separators of the nuspec and a missing leading folder.
    ''' </summary>
    Private Function findEntry(zip As ZipArchive, key As String) As ZipArchiveEntry
        Dim entry As ZipArchiveEntry = zip.Entries _
            .FirstOrDefault(Function(e) e.FullName.Equals(key, StringComparison.OrdinalIgnoreCase))

        If entry IsNot Nothing Then
            Return entry
        End If

        ' fall back to a suffix match so that a readme declared as ``README.md``
        ' is still found when it was packaged inside a sub folder.
        Return zip.Entries _
            .FirstOrDefault(Function(e) e.FullName.EndsWith("/" & key, StringComparison.OrdinalIgnoreCase))
    End Function

    Private Function findNuspec(zip As ZipArchive) As ZipArchiveEntry
        Dim rootEntry As ZipArchiveEntry = zip.Entries _
            .FirstOrDefault(Function(e) Not e.FullName.Contains("/"c) AndAlso
                                        e.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase))

        If rootEntry IsNot Nothing Then
            Return rootEntry
        End If

        Return zip.Entries _
            .FirstOrDefault(Function(e) e.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase))
    End Function

    Private Function parse(document As XDocument) As NupkgMetadata
        Dim metadata As XElement = document.Descendants() _
            .FirstOrDefault(Function(e) e.Name.LocalName = "metadata")

        If metadata Is Nothing Then
            Throw New InvalidDataException("invalid nuspec manifest: the metadata element is missing.")
        End If

        Dim result As New NupkgMetadata With {
            .Id = childValue(metadata, "id"),
            .Version = childValue(metadata, "version"),
            .Title = childValue(metadata, "title"),
            .Authors = childValue(metadata, "authors"),
            .Owners = childValue(metadata, "owners"),
            .Description = childValue(metadata, "description"),
            .Summary = childValue(metadata, "summary"),
            .ReleaseNotes = childValue(metadata, "releaseNotes"),
            .Copyright = childValue(metadata, "copyright"),
            .Language = childValue(metadata, "language"),
            .Tags = childValue(metadata, "tags"),
            .ProjectUrl = childValue(metadata, "projectUrl"),
            .LicenseUrl = childValue(metadata, "licenseUrl"),
            .License = readLicense(metadata),
            .RequireLicenseAcceptance = childValue(metadata, "requireLicenseAcceptance"),
            .Repository = readRepository(metadata),
            .Icon = childValue(metadata, "icon"),
            .Readme = childValue(metadata, "readme")
        }

        readDependencies(metadata, result)

        Return result
    End Function

    Private Function childValue(metadata As XElement, name As String) As String
        Dim element As XElement = metadata.Elements() _
            .FirstOrDefault(Function(e) e.Name.LocalName = name)

        If element Is Nothing Then
            Return ""
        End If

        Return element.Value.Trim()
    End Function

    Private Function readLicense(metadata As XElement) As String
        Dim element As XElement = metadata.Elements() _
            .FirstOrDefault(Function(e) e.Name.LocalName = "license")

        If element Is Nothing Then
            Return ""
        End If

        If Not String.IsNullOrEmpty(element.Value) Then
            Return element.Value.Trim()
        End If

        Dim typeAttribute As XAttribute = element.Attribute("type")
        If typeAttribute Is Nothing Then
            Return ""
        End If

        Return $"{typeAttribute.Value} license"
    End Function

    Private Function readRepository(metadata As XElement) As String
        Dim element As XElement = metadata.Elements() _
            .FirstOrDefault(Function(e) e.Name.LocalName = "repository")

        If element Is Nothing Then
            Return ""
        End If

        Dim url As XAttribute = element.Attribute("url")
        Return If(url Is Nothing, "", url.Value.Trim())
    End Function

    Private Sub readDependencies(metadata As XElement, result As NupkgMetadata)
        Dim dependencies As XElement = metadata.Elements() _
            .FirstOrDefault(Function(e) e.Name.LocalName = "dependencies")

        If dependencies Is Nothing Then
            Return
        End If

        Dim encoded As New List(Of String)

        ' grouped form: <group targetFramework="..."><dependency/></group>
        For Each group As XElement In dependencies.Elements() _
                .Where(Function(e) e.Name.LocalName = "group")

            Dim framework As String = attributeValue(group, "targetFramework")

            For Each dependency As XElement In group.Elements() _
                    .Where(Function(e) e.Name.LocalName = "dependency")
                addDependency(dependency, framework, result, encoded)
            Next
        Next

        ' flat form: <dependency/> directly under <dependencies/>
        For Each dependency As XElement In dependencies.Elements() _
                .Where(Function(e) e.Name.LocalName = "dependency")
            addDependency(dependency, "", result, encoded)
        Next

        result.Dependencies = String.Join(";", encoded)
    End Sub

    Private Sub addDependency(dependency As XElement, framework As String, result As NupkgMetadata, encoded As List(Of String))
        Dim name As String = attributeValue(dependency, "id")
        Dim range As String = attributeValue(dependency, "version")

        If String.IsNullOrEmpty(name) Then
            Return
        End If

        Call result.DependencyItems.Add(New NuspecDependency With {
            .targetFramework = framework,
            .id = name,
            .range = range
        })
        Call encoded.Add($"{name}|{range}")
    End Sub

    Private Function attributeValue(element As XElement, name As String) As String
        Dim attribute As XAttribute = element.Attribute(name)
        Return If(attribute Is Nothing, "", attribute.Value.Trim())
    End Function
End Module
