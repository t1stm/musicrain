using System.Text;

namespace Gaida.Core.Utils;

public static class Romanize
{
    public static string FromCyrillic(ReadOnlySpan<char> cyrillicText)
    {
        var builder = new StringBuilder();
        foreach (var c in cyrillicText)
        {
            var romanized = CyrillicToRomanSwitch(c);
            builder.Append(char.IsLower(c) ? romanized :
                romanized.Length > 1 ? char.ToUpper(romanized[0]) + romanized[1..] : char.ToUpper(romanized[0]));
        }

        return builder.ToString();
    }

    private static string CyrillicToRomanSwitch(char letter)
    {
        return char.ToLower(letter) switch
        {
            'в' => "v",
            'е' => "e",
            'р' => "r",
            'т' => "t",
            'ъ' => "u",
            'у' => "u",
            'и' => "i",
            'о' => "o",
            'п' => "p",
            'а' => "a",
            'с' => "s",
            'д' => "d",
            'ф' => "f",
            'г' => "g",
            'х' => "h",
            'й' => "y",
            'к' => "k",
            'л' => "l",
            'з' => "z",
            'ь' => "y",
            'ц' => "c",
            'б' => "b",
            'н' => "n",
            'м' => "m",

            'я' => "ya",
            'ж' => "zh",
            'ч' => "ch",
            'ш' => "sh",
            'щ' => "sht",
            'ю' => "yu",

            // Russian characters
            'ы' => "y",
            _ => letter.ToString()
        };
    }
}
