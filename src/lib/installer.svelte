<script lang='ts'>
    import { onMount } from 'svelte';
    import type BasicProps from './basicProps';
    import { invoke } from '@tauri-apps/api/core';
    import type { Nullable } from './nullable';
    import { open } from '@tauri-apps/plugin-dialog';

    let {
        canAdvance = $bindable(),
    }: BasicProps = $props();

    const SeaOfStarsRepo = "https://raw.githubusercontent.com/Ottermandias/SeaOfStars/main/repo.json";
    const SeaOfStarsStartsWith = "https://raw.githubusercontent.com/Ottermandias/SeaOfStars/";
    const PenumbraInternalName = "Penumbra";
    const HeliosphereInternalName = "heliosphere-plugin";

    let statuses: string[] = $state([]);
    let error: Nullable<string> = $state(undefined);
    let showPrompt: [boolean | string, any] = $state([false, undefined]);
    let configModified = false;

    onMount(() => {
        canAdvance = false;
        showPrompt = [false, undefined];
        statuses = [];
        configModified = false;
        error = undefined;
        start();
    });

    async function start() {
        try {
            await startInner();
        } catch (e) {
            error = new String(e) as string;
            console.error(e);
        }
    }

    async function startInner() {
        statuses.push('loading Dalamud configuration file');
        const json = await invoke<Nullable<string>>('get_dalamud_config_json');
        if (json == null) {
            error = 'could not read dalamudConfig.json';
            return;
        }

        statuses.push('parsing Dalamud configuration file');
        const config = JSON.parse(json);


        statuses.push('checking for Sea of Stars repository');
        console.dir(config);
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
            statuses.push('adding Sea of Stars repository');

            configModified = true;
            const repoJson = await invoke<Nullable<string>>('create_repo', {
                url: SeaOfStarsRepo,
            });

            if (repoJson == null) {
                throw new Error('failed to make repo');
            }

            trl.push(JSON.parse(repoJson));
        }

        console.dir(config);

        statuses.push('downloading plugin information from Sea of Stars');
        const resp = await fetch(already || SeaOfStarsRepo);
        const repo = await resp.json() as any[];
        const heliospherePlugin = repo.find(plugin => plugin['InternalName'] === HeliosphereInternalName);
        const penumbraPlugin = repo.find(plugin => plugin['InternalName'] === PenumbraInternalName);

        let penumbraNew = await installPlugin(penumbraPlugin, config, already || SeaOfStarsRepo);
        let heliosphereNew = await installPlugin(heliospherePlugin, config, already || SeaOfStarsRepo);

        configModified = configModified || penumbraNew || heliosphereNew;

        if (configModified) {
            statuses.push('saving Dalamud configuration file');
            const result = await invoke('write_dalamud_config_json', {
                json: JSON.stringify(config, undefined, 4),
            });
            console.log(result);
            //if (!result) {
            //    error = 'could not save Dalamud configuration file';
            //    return;
            //}
        }

        statuses.push('checking Penumbra mod directory');
        let penumbraConfig: any = undefined;
        try {
            const json = await invoke<string>('get_plugin_config_json', {
                internalName: penumbraPlugin['InternalName'],
            });

            penumbraConfig = JSON.parse(json);
        } catch {
            // no-op
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
            statuses.push('prompting for new Penumbra directory');
        }

        canAdvance = !showPrompt[0];
    }

    async function installPlugin(plugin: any, config: any, repoUrl: string): Promise<boolean> {
        const name = plugin['InternalName'];

        statuses.push(`checking if ${name} is already installed`);
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
                statuses.push(`${name} was already installed`);
                return false;
            }
        }

        statuses.push(`installing ${name}`);

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

        plugins.push(JSON.parse(pluginJson));
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
        statuses.push('updating Penumbra config');

        showPrompt[0] = false;

        let config = showPrompt[1];
        if (config == null) {
            config = {};
        }

        config['ModDirectory'] = path;

        statuses.push('saving Penumbra config');

        await invoke('write_plugin_config_json', {
            internalName: PenumbraInternalName,
            json: JSON.stringify(config, undefined, 4),
        });

        canAdvance = true;
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
        {error}
    {/if}

    {#if showPrompt[0] !== false}
        <strong>
            Where would you like your mods to be stored?
        </strong>

        <p>
            Pick a short location close to the root of a drive like
            <code>C:\Penumbra</code> or <code>C:\FFXIVMods</code>.
        </p>

        <button
            onclick={choosePenumbraDir}
        >
            Pick folder
        </button>

        {#if typeof showPrompt[0] === 'string'}
            <div class='error'>
                <small>
                    {showPrompt[0]}
                </small>
            </div>
        {/if}
    {/if}

    {#if statuses.length > 0}
        <div
            class='statuses'
            class:muted={error != null || showPrompt[0] !== false}
        >
            <strong>{ statuses[statuses.length - 1] }</strong>
            <ul>
                {#each [...statuses].reverse().slice(1) as status}
                    <li>{status}</li>
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
    }
</style>
