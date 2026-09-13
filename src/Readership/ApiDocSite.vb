Imports System.IO
Imports System.Text
Imports Microsoft.VisualBasic.ApplicationServices.Development.XmlDoc.Assembly
Imports Microsoft.VisualBasic.ApplicationServices.Development.XmlDoc.Serialization
Imports Microsoft.VisualBasic.Linq

''' <summary>
''' A namespace page entry of the generated api reference document site.
''' </summary>
Public Class DocNamespaceEntry

    ''' <summary>
    ''' the full name of this namespace
    ''' </summary>
    ''' <returns></returns>
    Public Property Name As String

    ''' <summary>
    ''' the name of the assembly that this namespace is defined in
    ''' </summary>
    ''' <returns></returns>
    Public Property Project As String

    ''' <summary>
    ''' the underlying xml comment document model
    ''' </summary>
    ''' <returns></returns>
    Public Property Source As ProjectNamespace

    ''' <summary>
    ''' the site relative url of this namespace page
    ''' </summary>
    ''' <returns></returns>
    Public Property Url As String

    ''' <summary>
    ''' the markdown document text of this namespace
    ''' </summary>
    ''' <returns></returns>
    Public Property Summary As String

    Public ReadOnly Property Types As New List(Of DocTypeEntry)
End Class

''' <summary>
''' A member (method / property / field / event) entry of a type page.
''' </summary>
Public Class DocMemberEntry

    ''' <summary>
    ''' M / P / F / E
    ''' </summary>
    ''' <returns></returns>
    Public Property Kind As Char

    ''' <summary>
    ''' the short name of this member
    ''' </summary>
    ''' <returns></returns>
    Public Property Name As String

    ''' <summary>
    ''' the full declare information, example as ``Ns.Type.Method(System.String)``
    ''' </summary>
    ''' <returns></returns>
    Public Property DeclareText As String

    ''' <summary>
    ''' the html anchor of this member inside its type page
    ''' </summary>
    ''' <returns></returns>
    Public Property Anchor As String

    ''' <summary>
    ''' the zero based overload index of this member
    ''' </summary>
    ''' <returns></returns>
    Public Property OverloadIndex As Integer

    Public Property Source As ProjectMember

    ''' <summary>
    ''' the human readable kind name, example as ``method``
    ''' </summary>
    ''' <returns></returns>
    Public ReadOnly Property KindName As String
        Get
            Select Case Kind
                Case "M"c
                    Return "method"
                Case "P"c
                    Return "property"
                Case "F"c
                    Return "field"
                Case "E"c
                    Return "event"
                Case Else
                    Return "member"
            End Select
        End Get
    End Property

    ''' <summary>
    ''' the signature text without the ``Ns.Type.`` prefix
    ''' </summary>
    ''' <returns></returns>
    Public Function Signature(typeEntry As DocTypeEntry) As String
        Dim decl$ = If(DeclareText, Name)

        If typeEntry IsNot Nothing AndAlso Not String.IsNullOrEmpty(typeEntry.FullName) Then
            If decl.StartsWith(typeEntry.FullName & ".", StringComparison.Ordinal) Then
                Return decl.Substring(typeEntry.FullName.Length + 1)
            End If
        End If

        Return decl
    End Function
End Class

''' <summary>
''' A type page entry of the generated api reference document site.
''' </summary>
Public Class DocTypeEntry

    ''' <summary>
    ''' the clr type short name, it may contains the generic arity suffix
    ''' </summary>
    ''' <returns></returns>
    Public Property Name As String

    ''' <summary>
    ''' the full name of this type, example as ``Ns.ClassName``
    ''' </summary>
    ''' <returns></returns>
    Public Property FullName As String

    ''' <summary>
    ''' the name of the assembly that this type is defined in
    ''' </summary>
    ''' <returns></returns>
    Public Property Project As String

    Public Property Source As ProjectType

    ''' <summary>
    ''' the site relative url of this type page
    ''' </summary>
    ''' <returns></returns>
    Public Property Url As String

    ''' <summary>
    ''' the containing namespace entry
    ''' </summary>
    ''' <returns></returns>
    Public Property ContainingNamespace As DocNamespaceEntry

    Public ReadOnly Property Members As New List(Of DocMemberEntry)

    ''' <summary>
    ''' get the members of a given kind, the result is grouped by the member name
    ''' and the overloads are merged into one group.
    ''' </summary>
    ''' <param name="kind"></param>
    ''' <returns></returns>
    Public Function GetMemberGroups(kind As Char) As IEnumerable(Of IGrouping(Of String, DocMemberEntry))
        Return Members _
            .Where(Function(m) m.Kind = kind) _
            .GroupBy(Function(m) m.Name)
    End Function

    ''' <summary>
    ''' get all of the members of a given kind, the overloads are flattened
    ''' </summary>
    ''' <param name="kind"></param>
    ''' <returns></returns>
    Public Function GetMembers(kind As Char) As IEnumerable(Of DocMemberEntry)
        Return Members.Where(Function(m) m.Kind = kind)
    End Function
End Class

''' <summary>
''' The in-memory navigation model of the generated api reference document site.
''' It indexes the namespaces, types and members parsed from the xml comment
''' documents, and it also builds the ``cref`` to page url index.
''' </summary>
Public Class ApiDocSite

    Public ReadOnly Property Projects As New List(Of Project)
    Public ReadOnly Property Namespaces As New List(Of DocNamespaceEntry)
    Public ReadOnly Property Types As New List(Of DocTypeEntry)

    ''' <summary>
    ''' the ``cref`` identity to the site relative url index
    ''' </summary>
    ''' <returns></returns>
    Public ReadOnly Property Xref As New Dictionary(Of String, String)(StringComparer.OrdinalIgnoreCase)

    Public ReadOnly Property Warnings As New List(Of String)

    Public ReadOnly Property MemberCount As Integer
        Get
            Return Types.Sum(Function(t) t.Members.Count)
        End Get
    End Property

    Public ReadOnly Property AssemblyNames As String()
        Get
            Return Projects.Select(Function(p) p.Name).Distinct().ToArray
        End Get
    End Property

    ''' <summary>
    ''' Build the site navigation model from the loaded xml comment document
    ''' projects.
    ''' </summary>
    ''' <param name="space"></param>
    ''' <param name="warnings"></param>
    ''' <returns></returns>
    Public Shared Function Build(space As IEnumerable(Of Project), Optional warnings As List(Of String) = Nothing) As ApiDocSite
        Dim site As New ApiDocSite
        Dim nsSlugs As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
        Dim typeSlugs As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)

        For Each proj As Project In space _
            .Where(Function(p) p IsNot Nothing) _
            .OrderBy(Function(p) p.Name)

            Call site.Projects.Add(proj)

            For Each ns As ProjectNamespace In proj.Namespaces _
                .Where(Function(n) n IsNot Nothing) _
                .OrderBy(Function(n) If(n.fullName, ""))

                Dim namespaceName$ = If(ns.fullName, "")
                Dim nsEntry As New DocNamespaceEntry With {
                    .Name = namespaceName,
                    .Project = proj.Name,
                    .Source = ns,
                    .Summary = namespaceSummary(ns),
                    .Url = PageUrl("namespaces", namespaceName, nsSlugs)
                }

                For Each t As ProjectType In ns.Types _
                    .Where(Function(x) x IsNot Nothing) _
                    .OrderBy(Function(x) x.Name)

                    Dim fullName$ = If(String.IsNullOrEmpty(namespaceName), t.Name, namespaceName & "." & t.Name)
                    Dim typeEntry As New DocTypeEntry With {
                        .Name = t.Name,
                        .FullName = fullName,
                        .Project = proj.Name,
                        .Source = t,
                        .ContainingNamespace = nsEntry,
                        .Url = PageUrl("types", fullName, typeSlugs)
                    }

                    Call buildMembers(typeEntry)
                    Call nsEntry.Types.Add(typeEntry)
                    Call site.Types.Add(typeEntry)
                Next

                Call site.Namespaces.Add(nsEntry)
            Next
        Next

        Call site.buildXref()

        If warnings IsNot Nothing Then
            warnings.AddRange(site.Warnings)
        End If

        Return site
    End Function

    ''' <summary>
    ''' Resolve a ``cref`` identity into a site relative url. Nothing will be
    ''' returned when the target could not be found in the current document site.
    ''' </summary>
    ''' <param name="cref"></param>
    ''' <returns></returns>
    Public Function ResolveUrl(cref As String) As String
        If String.IsNullOrWhiteSpace(cref) Then
            Return Nothing
        End If

        Dim id$ = cref.Trim()
        Dim url As String = Nothing

        If Xref.TryGetValue(id, url) Then
            Return url
        End If

        Dim kind As Char = " "c
        Dim body$ = id

        If id.Length > 2 AndAlso id(1) = ":"c Then
            kind = id(0)
            body = id.Substring(2)
        End If

        If Xref.TryGetValue(body, url) Then
            Return url
        End If

        Dim noParams$ = StripParams(body)

        If noParams <> body Then
            If Xref.TryGetValue(noParams, url) Then
                Return url
            End If

            If kind <> " "c AndAlso Xref.TryGetValue(kind & ":" & noParams, url) Then
                Return url
            End If

            ' fallback to the owner type of the referenced member
            Dim p = noParams.LastIndexOf("."c)

            If p > 0 Then
                Dim owner$ = noParams.Substring(0, p)

                If Xref.TryGetValue("T:" & owner, url) Then
                    Return url
                End If

                If Xref.TryGetValue(owner, url) Then
                    Return url
                End If
            End If
        Else
            Dim p = body.LastIndexOf("."c)

            If p > 0 AndAlso Xref.TryGetValue("T:" & body.Substring(0, p), url) Then
                Return url
            End If
        End If

        ' the last resort: the short name only
        Dim tokens = noParams.Split("."c)
        Dim shortName$ = tokens(tokens.Length - 1)

        If Xref.TryGetValue(shortName, url) Then
            Return url
        End If

        Return Nothing
    End Function

    Private Sub buildXref()
        For Each ns As DocNamespaceEntry In Namespaces
            Call addXref("N:" & ns.Name, ns.Url)
            Call addXref(ns.Name, ns.Url)
        Next

        For Each t As DocTypeEntry In Types
            Call addXref("T:" & t.FullName, t.Url)
            Call addXref(t.FullName, t.Url)
            Call addXref(t.Name, t.Url)

            For Each m As DocMemberEntry In t.Members
                Dim target$ = t.Url & "#" & m.Anchor
                Dim decl$ = If(m.DeclareText, m.Name)
                Dim noParams$ = StripParams(decl)

                Call addXref(m.Kind & ":" & decl, target)
                Call addXref(decl, target)

                If noParams <> decl Then
                    Call addXref(m.Kind & ":" & noParams, target)
                    Call addXref(noParams, target)
                End If
            Next
        Next
    End Sub

    Private Sub addXref(key As String, url As String)
        If String.IsNullOrWhiteSpace(key) OrElse String.IsNullOrWhiteSpace(url) Then
            Return
        End If

        If Not Xref.ContainsKey(key) Then
            Xref(key) = url
        End If
    End Sub

    Private Shared Function namespaceSummary(ns As ProjectNamespace) As String
        Return ns.Types _
            .Where(Function(t) t IsNot Nothing AndAlso t.Name = APIExtensions.NamespaceDoc) _
            .Select(Function(t) t.Summary) _
            .Where(Function(s) Not String.IsNullOrWhiteSpace(s)) _
            .Distinct _
            .JoinBy(vbLf & vbLf)
    End Function

    Private Shared Sub buildMembers(typeEntry As DocTypeEntry)
        Dim src As ProjectType = typeEntry.Source

        Call addMembers(typeEntry, "M"c, src.AllMethods)
        Call addMembers(typeEntry, "P"c, src.AllProperties)
        Call addMembers(typeEntry, "F"c, src.AllFields)
        Call addMembers(typeEntry, "E"c, src.AllEvents)
    End Sub

    Private Shared Sub addMembers(typeEntry As DocTypeEntry, kind As Char, members As IEnumerable(Of ProjectMember))
        If members Is Nothing Then
            Return
        End If

        For Each g In members _
            .Where(Function(m) m IsNot Nothing) _
            .GroupBy(Function(m) If(m.Name, "")) _
            .OrderBy(Function(x) x.Key)

            Dim overloadList = g.OrderBy(Function(m) If(m.[Declare], "")).ToList

            For i As Integer = 1 To overloadList.Count
                Dim m As ProjectMember = overloadList(i - 1)
                Dim anchor$ = "member-" & Char.ToLowerInvariant(kind) & "-" & Slug(g.Key) &
                    If(i = 1, "", "-" & i.ToString)

                Call typeEntry.Members.Add(New DocMemberEntry With {
                    .Kind = kind,
                    .Name = g.Key,
                    .DeclareText = If(m.[Declare], g.Key),
                    .Anchor = anchor,
                    .OverloadIndex = i - 1,
                    .Source = m
                })
            Next
        Next
    End Sub

    Private Shared Function PageUrl(folder As String, key As String, used As HashSet(Of String)) As String
        Dim baseName$ = Slug(key)
        Dim unique$ = baseName
        Dim n As Integer = 1

        While used.Contains(unique)
            n += 1
            unique = baseName & "-" & n.ToString
        End While

        Call used.Add(unique)

        Return folder & "/" & unique & ".html"
    End Function

    Private Shared Function StripParams(name As String) As String
        If String.IsNullOrEmpty(name) Then
            Return ""
        End If

        Dim p = name.IndexOf("("c)

        If p > 0 Then
            name = name.Substring(0, p)
        End If

        Return name
    End Function

    ''' <summary>
    ''' convert a namespace / type / member name into a safe file name fragment
    ''' </summary>
    ''' <param name="text"></param>
    ''' <returns></returns>
    Public Shared Function Slug(text As String) As String
        Dim sb As New StringBuilder

        For Each c As Char In If(text, "")
            If Char.IsLetterOrDigit(c) OrElse c = "."c OrElse c = "_"c OrElse c = "-"c Then
                sb.Append(c)
            Else
                sb.Append("_"c)
            End If
        Next

        Dim s$ = sb.ToString.TrimEnd("."c, " "c)

        If String.IsNullOrEmpty(s) Then
            s = "unnamed"
        End If

        If s.Length > 100 Then
            s = s.Substring(0, 100) & "-" & StableHash(text)
        End If

        Return s
    End Function

    ''' <summary>
    ''' a process independent string hash, the <see cref="String.GetHashCode"/> is
    ''' randomized between the process runs so it could not be used for the
    ''' stable file naming.
    ''' </summary>
    ''' <param name="text"></param>
    ''' <returns></returns>
    Private Shared Function StableHash(text As String) As String
        Dim h As Long = 17

        For Each c As Char In If(text, "")
            h = (h * 31 + AscW(c)) Mod 1000000007L
        Next

        Return h.ToString("x")
    End Function
End Class
