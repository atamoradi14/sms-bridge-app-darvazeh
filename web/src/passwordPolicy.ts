const common=new Set(["password","password1","password123","password1234","password1!","p@ssw0rd","p@ssw0rd!","p@ssw0rd1!","12345678","123456789","1234567890","qwerty123","qwerty123!","qwertyuiop","qwerty12","qwerty1!","abcdefgh","abcd1234","abc12345","11111111","00000000","87654321","1234abcd","iloveyou","iloveyou1","letmein123","letmein1!","welcome1","welcome1!","welcome123","admin123","admin1234","admin123!","changeme","changeme1!","changeme123","passw0rd","passw0rd!","trustno1","football","sunshine","princess","monkey123"]);
export function passwordError(value:string,confirmation:string,email:string,username:string){
 const chars=[...value];const trimmed=value.trim();
 if(chars.length<9||chars.length>128||/[\p{Cc}]/u.test(value))return "password_length";
 if(value!==confirmation)return "password_mismatch";
 if(trimmed.length<9||common.has(trimmed.toLowerCase())||/^\p{Nd}+$/u.test(value)||new Set(chars).size<3)return "password_common";
 for(let n=1;n<=4;n++)if(chars.length%n===0&&chars.every((c,i)=>c===chars[i%n]))return "password_common";
 const letters=trimmed.replace(/[^\p{L}]/gu,"").toLowerCase();
 for(const item of [username,email.split("@")[0]]){const name=item.replace(/[^\p{L}]/gu,"").toLowerCase();if(name.length>=3&&name===letters)return "password_personal";}
 return null;
}
