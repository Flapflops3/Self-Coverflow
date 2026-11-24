internal static class Edition
{
#if ENGLISH
    internal const string Profile="CoverShelf-EN", Locale="en-US", Suffix="-EN";
    internal static string Text(string chinese,string english) { return english; }
#else
    internal const string Profile="CoverShelf", Locale="zh-CN", Suffix="";
    internal static string Text(string chinese,string english) { return chinese; }
#endif
}
