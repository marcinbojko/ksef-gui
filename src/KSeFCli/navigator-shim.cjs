// Shim for pdfmake browser build running in Node.js.
// pdfmake/build/pdfmake.js references `navigator` (via FileSaver.js)
// which does not exist in Node.js.
if (typeof navigator === 'undefined') {
    global.navigator = { userAgent: 'node' };
}
if (typeof window === 'undefined') {
    global.window = {};
}
