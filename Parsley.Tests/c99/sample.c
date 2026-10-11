# 1 "sample.c"
#pragma once
typedef unsigned long size_t;
typedef struct node { int value; struct node *next; } node_t;
typedef int (*cmp_fn)(const void *, const void *);
enum color { RED, GREEN = 2, BLUE, };
static const char *names[] = { "red", [2] = "green" "ish", };
struct point { int x, y; unsigned flags : 3; int : 0; };
extern int printf(const char * restrict fmt, ...);
inline static double scale(double v, double f) { return v * f; }
int old_style(a, b) int a; char *b; { return a + *b; }
void vla(int n, int m[static 10], int k[const *], int (*grid)[n]);
_Bool flag = 1;
_Complex double z;
int main(void)
{
    node_t *head = 0;
    size_t count = sizeof(node_t) + sizeof head + sizeof(int[4]);
    struct point p = { .x = 1, .y = 2 };
    struct point *pp = &(struct point){ 3, 4 };
    int arr<:3:> = <% 1, 2, 3 %>;
    cmp_fn f = 0;
    double d = (double)count * 1.5e-3 + 0x1.8p1 + .5f + 1.;
    long long big = 0xFFFFFFFFull + 077L + 10u;
    char c = '\n', q = '\'', w = L'x', o = '\x41';
    unsigned *up = (unsigned *)&count;
    int (*fp)(int) = 0;
    x: ;
    for (int i = 0; i < 10; ++i) { count += i; if (i & 1) continue; else if (i > 5) break; else count--; }
    for (;;) break;
    while (count > 0) count >>= 1;
    do { count++; } while (count < 3);
    switch (count) { case 1: case 2: count = 0; break; default: goto x; }
    count = count ? count : (size_t)-1;
    p.x = pp->y++ + arr[1] * -arr[2] % 3 << 1;
    head = head->next ? head->next->next : (node_t *)0;
    count = (size_t)sizeof(struct point) / 2, d /= 2;
    printf("%d %s\n", p.x, names[0]);
    /* block
       comment */
    // line comment
    return f ? f(&p, &p) : scale(d, 2.0) > 1 && !flag || ~p.y;
}
