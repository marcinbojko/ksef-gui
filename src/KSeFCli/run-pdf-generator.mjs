#!/usr/bin/env node
import { readFile, writeFile } from 'fs/promises';
import { JSDOM } from 'jsdom';
import { createRequire } from 'module';
import { fileURLToPath } from 'url';
import { dirname, join } from 'path';

const [repoPath, inputXmlPath, outputPdfPath] = process.argv.slice(2);

if (!repoPath || !inputXmlPath || !outputPdfPath) {
    console.error('Usage: node run-pdf-generator.mjs <repoPath> <inputXml> <outputPdf>');
    process.exit(1);
}


const makeRequire = (repoPath) => (modulePath) => createRequire(join(repoPath, 'package.json'))(modulePath);

const requireFromRepo = makeRequire(repoPath);

const { JSDOM } = requireFromRepo('jsdom/lib/api.js');
const pdfMake = requireFromRepo('pdfmake/build/pdfmake.js');
const vfs = requireFromRepo('pdfmake/build/vfs_fonts.js');
const { generateInvoice } = requireFromRepo('ksef-fe-invoice-converter.js');

const dom = new JSDOM('<!DOCTYPE html><html><body></body></html>', {
    url: 'http://localhost',
    pretendToBeVisual: true,
    resources: 'usable'
});

global.window = dom.window;
global.document = dom.window.document;
global.File = dom.window.File;
global.Blob = dom.window.Blob;
global.FileReader = dom.window.FileReader;
global.navigator = dom.window.navigator;

pdfMake.vfs = vfs.pdfMake.vfs;
global.pdfMake = pdfMake;

async function main() {
    try {
        const xmlBuffer = await readFile(inputXmlPath);
        const xmlFile = new File([xmlBuffer], inputXmlPath, { type: 'application/xml' });

        const pdfBlob = await generateInvoice(xmlFile, {}, 'blob');

        const buffer = await new Promise((resolve, reject) => {
            const reader = new FileReader();
            reader.onload = () => resolve(Buffer.from(reader.result));
            reader.onerror = reject;
            reader.readAsArrayBuffer(pdfBlob);
        });

        await writeFile(outputPdfPath, buffer);
        console.log(`PDF generated: ${outputPdfPath}`);
    } catch (error) {
        console.error('Error:', error.message);
        process.exit(1);
    }
}

main();
