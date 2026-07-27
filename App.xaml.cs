using System;
using System.Linq;
using System.Windows;

namespace BackupUtility
{
    public partial class App : Application
    {
        public static void SetLanguage(string languageCode)
        {
            var dictionaries = Current.Resources.MergedDictionaries;
            var active = dictionaries.FirstOrDefault(dictionary => dictionary.Source?.OriginalString.Contains("Strings.", StringComparison.OrdinalIgnoreCase) == true);
            if (active is not null)
                dictionaries.Remove(active);
            dictionaries.Add(new ResourceDictionary
            {
                Source = new Uri($"Resources/Strings.{languageCode}.xaml", UriKind.Relative)
            });
        }
    }
}
