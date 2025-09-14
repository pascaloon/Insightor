using System;


Console.WriteLine(IsPalindrome("racecar"));
Console.WriteLine(IsPalindrome("hello"));


static bool IsPalindrome(string s)
{
    if (s == null) return false;
    int left = 0, right = s.Length - 1;
    while (left < right)
    {
        if (s[left] != s[right])
            return false;
        left++;
        right--;
    }
    return true;
}