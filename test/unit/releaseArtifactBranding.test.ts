import { describe, expect, it } from 'vitest';
import fs from 'node:fs';
import path from 'node:path';

// test/unit/releaseArtifactBranding.test.ts
// Guards the names that leave the repository in packaged desktop artifacts and GitHub Release drafts.

const root = path.resolve(__dirname, '../..');
const read = (relativePath: string) => fs.readFileSync(path.join(root, relativePath), 'utf8');

describe('release artifact branding', () => {
    it('publishes desktop artifacts under LumaMusic names', () => {
        const pkg = JSON.parse(read('package.json'));

        expect(pkg.build.publish).toEqual([{ provider: 'github', owner: 'QCJLchina', repo: 'LumaMusic' }]);
        expect(pkg.desktopName).toBe('lumamusic.desktop');
        expect(pkg.build.win.artifactName).toBe('${productName}-Setup-${version}.${ext}');
        expect(pkg.build.linux.executableName).toBe('lumamusic');
        expect(pkg.build.linux.desktop.entry.StartupWMClass).toBe('lumamusic');
        expect(pkg.build.win.extraResources[0].filter).toEqual(['lumamusic-wallpaper-helper.exe']);
        expect(pkg.build.extraResources).toContainEqual({
            from: 'packaging/linux/lumamusic.desktop',
            to: 'linux/lumamusic.desktop',
        });
    });

    it.each(['.github/release-draft-template.md', '.github/release-candidate-template.md'])(
        'keeps %s free of upstream artifact names',
        (templatePath) => {
            const template = read(templatePath);

            expect(template).toContain('LumaMusic-Setup-<version>.exe');
            expect(template).toContain('lumamusic-<version>-linux-x64.tar.gz');
            expect(template).toContain('yay -S lumamusic-bin');
            expect(template).not.toContain('Folia-Setup');
            expect(template).not.toContain('folia-major');
        },
    );

    it('keeps the AUR package and workflow on the LumaMusic repository and names', () => {
        const pkgbuild = read('packaging/aur/lumamusic-bin/PKGBUILD');
        const releaseWorkflow = read('.github/workflows/electron-release.yml');
        const arm64Check = read('.github/workflows/linux-arm64-package-check.yml');

        expect(pkgbuild).toContain('pkgname=lumamusic-bin');
        expect(pkgbuild).toContain('https://github.com/QCJLchina/LumaMusic');
        expect(pkgbuild).toContain('lumamusic-${pkgver}-linux-x64.tar.gz');
        expect(releaseWorkflow).toContain('packaging/aur/lumamusic-bin/update.sh');
        expect(releaseWorkflow).not.toContain('packaging/aur/folia-major-bin');
        expect(arm64Check).toContain('release/lumamusic-${version}-linux-arm64.tar.gz');
        expect(arm64Check).toContain('${app}/lumamusic');
    });
});
