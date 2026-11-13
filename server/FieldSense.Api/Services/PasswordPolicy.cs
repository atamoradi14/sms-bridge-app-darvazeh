using System.Text;
namespace FieldSense.Api.Services;
public static class PasswordPolicy
{
    private static readonly HashSet<string> Common=new(StringComparer.OrdinalIgnoreCase) {
        "password","password1","password123","password1234","password1!","p@ssw0rd","p@ssw0rd!","p@ssw0rd1!",
        "12345678","123456789","1234567890","qwerty123","qwerty123!","qwertyuiop","qwerty12","qwerty1!",
        "abcdefgh","abcd1234","abc12345","11111111","00000000","87654321","1234abcd","iloveyou","iloveyou1",
        "letmein123","letmein1!","welcome1","welcome1!","welcome123","admin123","admin1234","admin123!",
        "changeme","changeme1!","changeme123","passw0rd","passw0rd!","trustno1","football","sunshine","princess","monkey123"
    };
    public static string? Error(string? password,string? confirmation,string? email=null,string? username=null) {
        if(password==null)return "password_required";
        var runes=password.EnumerateRunes().ToArray();
        if(runes.Length is <9 or >128 || password.Any(char.IsControl))return "password_length";
        if(!string.Equals(password,confirmation,StringComparison.Ordinal))return "password_mismatch";
        var trimmed=password.Trim();
        var letters=new string(trimmed.Where(char.IsLetter).ToArray()).ToLowerInvariant();
        if(trimmed.Length<9 || Common.Contains(trimmed) || runes.All(r=>Rune.IsDigit(r)) || runes.Distinct().Count()<3)return "password_common";
        for(var period=1;period<=4;period++)
            if(runes.Length%period==0 && runes.Select((r,i)=>r==runes[i%period]).All(s=>s))return "password_common";
        foreach(var identity in new[]{username,email?.Split('@')[0]}) {
            var name=new string((identity??"").Where(char.IsLetter).ToArray()).ToLowerInvariant();
            if(name.Length>=3 && letters==name)return "password_personal";
        }
        return null;
    }
}
