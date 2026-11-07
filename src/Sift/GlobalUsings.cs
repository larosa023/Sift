// WPF projects drop System.IO from implicit usings (to avoid a clash with System.Windows.Shapes.Path).
// Sift never imports Shapes, so bring it back everywhere.
global using System.IO;
global using System.Net.Http;
