int Fact(int x)
{
    if (x <= 1)
        return 1;
    return x * Fact(x - 1);
}


int Add(int x, int y)
{
    return x + y;
}

int Magic(int x, int y)
{
    return Add(x, y) * Add(x * 2, y * 2);
}

int a = 3;
int b = 4;
int z = Magic(a, b);
int z2 = Add(a*2, b*2);

for (int i = 0; i < 5; i++)
{
    a = Add(a, 2);
}

int z3 = Fact(3);


// int a = 2;
// int b = 3;

// int Sum(int x, int y)
// {
//     int z = x + y;
//     return z;
// }

// for (int i = 0; i < 5; ++i)
// {
//     Console.WriteLine(Sum(a, i));
// }

// Example usage:
// BinarySearch(new int[] { 1, 3, 5, 7, 9 }, 77);

// static int BinarySearch(int[] arr, int target)
// {
//     int left = 0, right = arr.Length - 1;
    
//     while (left <= right)
//     {
//         int mid = left + (right - left) / 2;
//         if (arr[mid] == target)
//             return mid;
//         else if (arr[mid] < target)
//             left = mid + 1;
//         else
//             right = mid - 1;
//     }
//     return -1;
// }

// int x = 5;
// int y = x + 4;

// var arr = new int[] { 1, 3, 5, 7, 9 };
// var arr2 = arr
//     .Where(x => {return x > 5;})
//     .Select(x => x *2)
//     .ToList();





// Console.WriteLine(IsPalindrome("racecar"));
// static bool IsPalindrome(string s)
// {
//     if (s == null) return false;
//     int left = 0, right = s.Length - 1;
//     while (left < right)
//     {
//         if (s[left] != s[right])
//             return false;
//         left++;
//         right--;
//     }
//     return true;
// }