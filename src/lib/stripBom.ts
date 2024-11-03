export default function stripBom(input: string): string {
    if (input.charCodeAt(0) === 0xFEFF) {
        return input.substring(1);
    }

    return input;
}
