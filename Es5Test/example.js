// A small ES5 sample that touches the tricky parts: regex vs division,
// automatic semicolon insertion, accessors, for-in, labels and new.
(function (global) {
	"use strict";

	var ratio = 10 / 2 / 5,          // division twice
		word = /^[a-z]+\/?$/i,       // a regex literal with an escaped /
		total = 0

	var counter = {
		count: 0,
		get double() { return this.count * 2; },
		set double(v) { this.count = v / 2; },
		"quoted key": [1, , 3,],
		default: null                // reserved words are fine as property names
	}

	function tally(items) {
		var key
		outer: for (key in items) {
			if (!items.hasOwnProperty(key)) continue
			for (var i = 0; i < 3; i++) {
				if (i === 2) continue outer
				total += items[key] ? 1 : 0
			}
		}
		return total
	}

	function check(s) {
		if (word.test(s)) /x/.test(s)  // a regex after an if condition
		return              // ASI: returns undefined; the next line is separate
		s
	}

	var a = 1
	var b = a
	++b                     // ASI: a separate ++b, not a++
	var made = new new Function("return Date")()()

	try {
		throw new Error("caught " + tally({ x: 1, y: 2 }))
	} catch (e) {
		global.message = e.message
	} finally {
		counter.double = 8
	}

	do counter.count--; while (counter.count > 0)

	switch (typeof made) {
		case "object": global.kind = "date"; break
		default: global.kind = "other"
	}
}(this))