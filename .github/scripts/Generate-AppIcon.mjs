import { execFileSync } from "node:child_process";
import { readFileSync, writeFileSync } from "node:fs";

const [sourcePath, ...outputPaths] = process.argv.slice(2);

if (!sourcePath || outputPaths.length === 0) {
	console.error("Usage: node Generate-AppIcon.mjs <source.png> <output.ico> [...output.ico]");
	process.exit(2);
}

const iconSizes = [16, 24, 32, 48, 64, 128, 256];
const source = readFileSync(sourcePath);
const pngSignature = Buffer.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);

if (!source.subarray(0, pngSignature.length).equals(pngSignature)) {
	throw new Error(`${sourcePath} is not a PNG image.`);
}

const renderFrame = (size) => {
	const png = execFileSync("ffmpeg", [
		"-v", "error",
		"-i", sourcePath,
		"-frames:v", "1",
		"-vf", `scale=${size}:${size}:flags=lanczos`,
		"-f", "image2pipe",
		"-vcodec", "png",
		"pipe:1",
	], { maxBuffer: 32 * 1024 * 1024 });

	if (!png.subarray(0, pngSignature.length).equals(pngSignature)
		|| png.readUInt32BE(16) !== size
		|| png.readUInt32BE(20) !== size) {
		throw new Error(`Failed to render ${size}x${size} PNG icon frame.`);
	}

	return png;
};

const frames = iconSizes.map((size) => ({ size, png: renderFrame(size) }));
const directorySize = 6 + frames.length * 16;
const directory = Buffer.alloc(directorySize);
directory.writeUInt16LE(0, 0); // Reserved.
directory.writeUInt16LE(1, 2); // ICO resource type.
directory.writeUInt16LE(frames.length, 4);

let imageOffset = directorySize;
for (let index = 0; index < frames.length; index++) {
	const { size, png } = frames[index];
	const entryOffset = 6 + index * 16;
	directory.writeUInt8(size === 256 ? 0 : size, entryOffset);
	directory.writeUInt8(size === 256 ? 0 : size, entryOffset + 1);
	directory.writeUInt8(0, entryOffset + 2); // Palette size.
	directory.writeUInt8(0, entryOffset + 3); // Reserved.
	directory.writeUInt16LE(1, entryOffset + 4); // Color planes.
	directory.writeUInt16LE(32, entryOffset + 6); // Bits per pixel.
	directory.writeUInt32LE(png.length, entryOffset + 8);
	directory.writeUInt32LE(imageOffset, entryOffset + 12);
	imageOffset += png.length;
}

const icon = Buffer.concat([directory, ...frames.map(({ png }) => png)]);
for (const outputPath of outputPaths) {
	writeFileSync(outputPath, icon);
	console.log(`Wrote ${outputPath} (${icon.length} bytes; ${iconSizes.join(", ")} px frames)`);
}
