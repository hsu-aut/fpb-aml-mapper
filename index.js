#!/usr/bin/env node
// CLI for fpb-aml-mapper

import { readFileSync, writeFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { jsonToAml } from './src/json-to-aml.js';
import { amlToJson } from './src/aml-to-json.js';

const [,, command, inputPath, outputPath] = process.argv;

if (!command || !inputPath) {
  console.error('Usage:');
  console.error('  node index.js to-aml <input.json> [output.aml]');
  console.error('  node index.js to-json <input.aml> [output.json]');
  process.exit(1);
}

const input = readFileSync(resolve(inputPath), 'utf-8');

if (command === 'to-aml') {
  const json = JSON.parse(input);
  const aml = jsonToAml(json);
  if (outputPath) {
    writeFileSync(resolve(outputPath), aml, 'utf-8');
    console.log(`Written to ${outputPath}`);
  } else {
    process.stdout.write(aml);
  }
} else if (command === 'to-json') {
  const json = amlToJson(input);
  const output = JSON.stringify(json, null, 4);
  if (outputPath) {
    writeFileSync(resolve(outputPath), output, 'utf-8');
    console.log(`Written to ${outputPath}`);
  } else {
    process.stdout.write(output);
  }
} else {
  console.error(`Unknown command: ${command}`);
  process.exit(1);
}
