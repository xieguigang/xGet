Imports System.IO
Imports System.Reflection

''' <summary>
''' The resolved theme of the generated document site.
''' </summary>
Public Class ThemeBundle

    ''' <summary>
    ''' the theme name
    ''' </summary>
    ''' <returns></returns>
    Public Property Name As String

    ''' <summary>
    ''' the site relative urls of the stylesheets
    ''' </summary>
    ''' <returns></returns>
    Public ReadOnly Property Css As New List(Of String)

    ''' <summary>
    ''' the site relative urls of the script files
    ''' </summary>
    ''' <returns></returns>
    Public ReadOnly Property Js As New List(Of String)

    ''' <summary>
    ''' does a favicon.png / favicon.ico file exists in the site root
    ''' </summary>
    ''' <returns></returns>
    Public Property HasFavicon As Boolean
End Class

''' <summary>
''' Resolve the theme of the generated document site and publish its static
''' assets into the output directory.
''' 
''' The theme option could be:
''' 
''' + the name of a built-in theme, the default ``scibasic`` theme is the dark
'''   technical minimalism theme which is aligned with the web front end of the
'''   nuget package server (``dist/wwwroot``);
''' + the path of an external theme directory, all of the files in that
'''   directory will be copied into the ``assets`` folder of the output site,
'''   every ``*.css`` and ``*.js`` file will be linked into the generated pages.
''' </summary>
Public Module ThemeAssets

    ''' <summary>
    ''' the name of the built-in default theme
    ''' </summary>
    Public Const DefaultTheme$ = "scibasic"

    ''' <summary>
    ''' Resolve the given theme and publish its static assets into the output
    ''' directory.
    ''' </summary>
    ''' <param name="theme">a built-in theme name or an external theme directory</param>
    ''' <param name="output">the output directory of the document site</param>
    ''' <returns></returns>
    Public Function Resolve(theme As String, output As String) As ThemeBundle
        Dim name$ = If(String.IsNullOrWhiteSpace(theme), DefaultTheme, theme.Trim())

        If Directory.Exists(name) Then
            Return publishExternal(name, output)
        End If

        Return publishBuiltin(name, output)
    End Function

    Private Function publishBuiltin(name As String, output As String) As ThemeBundle
        If Not String.Equals(name, DefaultTheme, StringComparison.OrdinalIgnoreCase) Then
            Throw New InvalidOperationException($"the theme was not found: {name}")
        End If

        Dim bundle As New ThemeBundle With {
            .Name = DefaultTheme,
            .HasFavicon = True
        }
        Dim asm As Assembly = GetType(ThemeAssets).Assembly

        Call writeResource(asm, ".Themes.scibasic.css", output, "assets/css/scibasic.css")
        Call writeResource(asm, ".Themes.docs.css", output, "assets/css/docs.css")
        Call writeResource(asm, ".Themes.docs.js", output, "assets/js/docs.js")
        Call writeResource(asm, ".Themes.favicon.png", output, "favicon.png")

        Call bundle.Css.Add("assets/css/scibasic.css")
        Call bundle.Css.Add("assets/css/docs.css")
        Call bundle.Js.Add("assets/js/docs.js")

        Return bundle
    End Function

    Private Function publishExternal(themeDir As String, output As String) As ThemeBundle
        Dim root$ = Path.GetFullPath(themeDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
        Dim bundle As New ThemeBundle With {
            .Name = Path.GetFileName(root)
        }

        For Each filePath As String In Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            Dim rel$ = filePath.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            Dim target$ = Path.Combine(output, "assets", rel)
            Dim siteUrl$ = "assets/" & rel.Replace(Path.DirectorySeparatorChar, "/"c).Replace(Path.AltDirectorySeparatorChar, "/"c)

            Call ensureParent(target)
            Call File.Copy(filePath, target, True)

            Select Case Path.GetExtension(filePath).ToLowerInvariant
                Case ".css"
                    Call bundle.Css.Add(siteUrl)
                Case ".js"
                    Call bundle.Js.Add(siteUrl)
            End Select
        Next

        bundle.Css.Sort()
        bundle.Js.Sort()

        For Each icon As String In {"favicon.png", "favicon.ico"}
            Dim src$ = Path.Combine(root, icon)

            If File.Exists(src) Then
                Call File.Copy(src, Path.Combine(output, icon), True)
                bundle.HasFavicon = True
                Exit For
            End If
        Next

        Return bundle
    End Function

    Private Sub writeResource(asm As Assembly, suffix As String, output As String, relative As String)
        Dim resourceName$ = asm _
            .GetManifestResourceNames _
            .FirstOrDefault(Function(name) name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))

        If resourceName Is Nothing Then
            Throw New InvalidOperationException($"the theme resource was not found in the assembly: {suffix}")
        End If

        Dim target$ = Path.Combine(output, relative.Replace("/"c, Path.DirectorySeparatorChar))
        Call ensureParent(target)

        Using input As Stream = asm.GetManifestResourceStream(resourceName)
            Using fs As New FileStream(target, FileMode.Create, FileAccess.Write)
                Call input.CopyTo(fs)
            End Using
        End Using
    End Sub

    Private Sub ensureParent(targetPath As String)
        Dim dir$ = Path.GetDirectoryName(targetPath)

        If Not String.IsNullOrEmpty(dir) Then
            Call Directory.CreateDirectory(dir)
        End If
    End Sub
End Module
