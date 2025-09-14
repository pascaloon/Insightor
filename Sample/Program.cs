int x = 1;

if (x > 0)
{
    x += 1;
}

int y = x + 2;
int z = add(x, y);

Console.WriteLine($"x = {x}, y = {y}, z = {z}");


int add(int a, int b)
{
    return a + b;
}