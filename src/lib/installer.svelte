<script lang='ts'>
    import { onMount } from 'svelte';
    import type BasicProps from './basicProps';
    import { invoke } from '@tauri-apps/api/core';
    import type { Nullable } from './nullable';
    import { open } from '@tauri-apps/plugin-dialog';
    import stripBom from './stripBom';

    let {
        canAdvance = $bindable(),
        canGoBack = $bindable(),
    }: BasicProps = $props();

    const SeaOfStarsRepo = "https://raw.githubusercontent.com/Ottermandias/SeaOfStars/main/repo.json";
    const SeaOfStarsStartsWith = "https://raw.githubusercontent.com/Ottermandias/SeaOfStars/";
    const PenumbraInternalName = "Penumbra";
    const HeliosphereInternalName = "heliosphere-plugin";

    let statuses: [string, number | undefined, number | undefined][] = $state([]);
    let error: Nullable<string> = $state(undefined);
    let showPrompt: [boolean | string, any] = $state([false, undefined]);
    let configModified = false;

    onMount(() => {
        restart();
    });

    function restart() {
        canGoBack = false;
        canAdvance = false;
        showPrompt = [false, undefined];
        statuses = [];
        configModified = false;
        error = undefined;
        start();
    }

    function pushStatus(status: string, progress?: number, total?: number) {
        statuses.push([status, progress, total]);
    }

    async function start() {
        try {
            await startInner();
        } catch (e) {
            error = new String(e) as string;
            console.error(e);
        }
    }

    async function startInner() {
        pushStatus('loading Dalamud configuration file');
        const json = await invoke<Nullable<string>>('get_dalamud_config_json');
        if (json == null) {
            error = 'could not read dalamudConfig.json';
            return;
        }

        pushStatus('parsing Dalamud configuration file');
        const config = JSON.parse(stripBom(json));


        pushStatus('checking for Sea of Stars repository');
        const trl = config['ThirdRepoList']['$values'] as any[];
        let already: Nullable<string> = undefined;
        for (const repo of trl) {
            const url = repo['Url'] as Nullable<string>;
            if (url == null) {
                continue;
            }

            if (url.toLowerCase().startsWith(SeaOfStarsStartsWith.toLowerCase())) {
                already = url;
                break;
            }
        }

        if (already == null) {
            pushStatus('adding Sea of Stars repository');

            configModified = true;
            const repoJson = await invoke<Nullable<string>>('create_repo', {
                url: SeaOfStarsRepo,
            });

            if (repoJson == null) {
                throw new Error('failed to make repo');
            }

            trl.push(JSON.parse(stripBom(repoJson)));
        }

        pushStatus('downloading plugin information from Sea of Stars');
        const resp = await fetch(already || SeaOfStarsRepo);
        const repo = await resp.json() as any[];
        const heliospherePlugin = repo.find(plugin => plugin['InternalName'] === HeliosphereInternalName);
        const penumbraPlugin = repo.find(plugin => plugin['InternalName'] === PenumbraInternalName);

        let penumbraNew = await installPlugin(penumbraPlugin, config, already || SeaOfStarsRepo);
        let heliosphereNew = await installPlugin(heliospherePlugin, config, already || SeaOfStarsRepo);

        configModified = configModified || penumbraNew || heliosphereNew;

        if (configModified) {
            pushStatus('saving Dalamud configuration file');
            await invoke('write_dalamud_config_json', {
                json: JSON.stringify(config, undefined, 4),
            });
        }

        pushStatus('checking Penumbra mod directory');
        let penumbraConfig: any = undefined;
        try {
            const json = await invoke<string>('get_plugin_config_json', {
                internalName: penumbraPlugin['InternalName'],
            });

            penumbraConfig = JSON.parse(stripBom(json));
        } catch (e) {
            // no-op
            console.error(e);
        }

        const dir = penumbraConfig?.['ModDirectory'];
        if (dir == null || dir.length === 0) {
            showPrompt = [true, penumbraConfig];
        } else {
            // check validity
            const valid = await invoke<boolean>('check_path_validity', {
                path: dir,
                create: true,
            });

            showPrompt = [!valid, penumbraConfig];
        }

        if (showPrompt[0] !== false) {
            pushStatus('prompting for new Penumbra directory');
        }

        canAdvance = !showPrompt[0];
    }

    async function installPlugin(plugin: any, config: any, repoUrl: string): Promise<boolean> {
        const name = plugin['InternalName'];

        pushStatus(`checking if ${name} is already installed`);
        const profile = config['DefaultProfile'];
        if (profile == null) {
            throw new Error('default profile was null');
        }

        const plugins = profile['Plugins']['$values'] as Nullable<any[]>;
        if (plugins == null) {
            throw new Error('default profile plugins was null');
        }

        for (const installed of plugins) {
            const installedName = installed['InternalName'];
            if (installedName === name) {
                pushStatus(`${name} was already installed`);
                return false;
            }
        }

        pushStatus(`installing ${name}`);

        const workingPluginId = await invoke<Nullable<string>>('install_plugin_from_url', {
            internalName: name,
            url: plugin['DownloadLinkInstall'],
            repoUrl,
        });

        if (workingPluginId == null) {
            throw new Error('install failed');
        }

        const pluginJson = await invoke<Nullable<string>>('create_plugin', {
            internalName: name,
            workingId: workingPluginId,
        });

        if (pluginJson == null) {
            throw new Error('could not create plugin');
        }

        plugins.push(JSON.parse(stripBom(pluginJson)));
        return true;
    }

    async function choosePenumbraDir() {
        const dir = await open({
            multiple: false,
            directory: true,
            title: 'Choose Penumbra root directory',
        });

        if (dir == null) {
            return;
        }

        const valid = await invoke<boolean>('check_path_validity', {
            path: dir,
            create: false,
        });

        if (!valid) {
            showPrompt[0] = 'Invalid directory. Pick a different one.';
            return;
        }

        await finishSetup(dir);
    }

    async function finishSetup(path: string) {
        pushStatus('updating Penumbra config');

        showPrompt[0] = false;

        let config = showPrompt[1];
        if (config == null) {
            config = {};
        }

        config['ModDirectory'] = path;

        pushStatus('saving Penumbra config');

        await invoke('write_plugin_config_json', {
            internalName: PenumbraInternalName,
            json: JSON.stringify(config, undefined, 4),
        });

        canAdvance = true;
    }

    const numFormat = new Intl.NumberFormat();

    function formatProgress(progress?: number, total?: number): string {
        if (progress == null && total == null) {
            return '';
        }

        let output = '(';
        output += progress == null ? '?' : numFormat.format(progress);

        if (total != null) {
            output += `/${numFormat.format(total)}`;
        }

        output += ')';

        return output;
    }
</script>

{#if canAdvance}
    <strong>Installed!</strong>

    <p>
        You're all set. You can now close this window and open the game. After
        logging in to a character, Heliosphere will prompt you to do a
        first-time setup.
    </p>
{:else}
    {#if error != null}
        <div class='error'>
            <div>
                {error}
            </div>

            <button
                class='fullwidth'
                onclick={restart}
            >
                Retry
            </button>
        </div>
    {/if}

    {#if showPrompt[0] !== false}
        <article class='prompt'>
            <header>
                <strong>
                    Where would you like your mods to be stored?
                </strong>
            </header>

            <p>
                Pick a short location close to the root of a drive like
                <code>C:\Penumbra</code> or <code>D:\FFXIVMods</code>.
            </p>

            <footer>
                {#if typeof showPrompt[0] === 'string'}
                    <div class='error'>
                        <small>
                            {showPrompt[0]}
                        </small>
                    </div>
                {/if}

                <button
                    onclick={choosePenumbraDir}
                >
                    Pick folder
                </button>
            </footer>
        </article>
    {/if}

    {#if statuses.length > 0}
        <div
            class='statuses'
            class:muted={error != null || showPrompt[0] !== false}
        >
            <strong aria-busy='true'>
                {statuses[statuses.length - 1][0]}
            </strong>
            <ul>
                {#each [...statuses].reverse().slice(1) as status}
                    <li>
                        {status[0]}
                        {formatProgress(status[1], status[2])}
                    </li>
                {/each}
            </ul>
        </div>
    {/if}
{/if}

<style lang='scss'>
    @use 'sass:color';
    @use '@picocss/pico/scss/colors' as *;

    .statuses.muted {
        opacity: .5;
    }

    .error {
        padding: var(--pico-spacing);
        border-radius: var(--pico-border-radius);
        background-color: #{color.mix($slate-900, $red-500, 95%)};
        color: #{color.mix($zinc-200, $red-500, 90%)};

        &:not(:only-child) {
            margin-bottom: var(--pico-spacing);
        }

        & > button {
            margin-top: var(--pico-spacing);
        }
    }
</style>
