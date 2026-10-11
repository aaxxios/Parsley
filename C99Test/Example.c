// preprocessor is not supported
//#include <stdio.h>
//#include <complex.h>
typedef struct {
    int bar;
} foo_t;
int main() {
    foo_t test;
    int n = 5;
    int array[n];
    for (int i = 0; i < n; i++) {
        array[i] = i;
        printf("%d ", array[i]);
    }
}
 