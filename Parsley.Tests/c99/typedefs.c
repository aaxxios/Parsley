typedef unsigned long size_t;
typedef struct node node;
struct node { int value; node *next; size_t size_t_field; };
typedef int (*cmp_fn)(const void *, const void *);
typedef int T;
int g(int T, long y);
size_t count(node *n) { size_t c = 0; while (n) { ++c; n = n->next; } return c; }
int shadow(void)
{
    T a = 1;
    T * b = &a;
    {
        long T = 2;
        T * a;
        int x = (int)T;
    }
    T c = (T)*b + sizeof(T) + sizeof (a);
    node n; n.next = (node *)0;
    struct node *p = &n;
    int arr[sizeof(T)];
    for (T i = 0; i < 3; ++i) { T j = i; }
    return c;
}
int params(T T) { return T * 2; }
int kr(a, b) T a; int b; { T z = a; return z + b; }
enum { size_t_like = 3, T2 = sizeof(T) };
