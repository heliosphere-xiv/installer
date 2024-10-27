<script lang='ts'>
    import { onMount } from 'svelte';
    import type BasicProps from './basicProps';
    import { invoke } from '@tauri-apps/api/core';
    import type { Nullable } from './nullable';

    let {
        canAdvance,
    }: BasicProps = $props();

    const SeaOfStarsRepo = "https://raw.githubusercontent.com/Ottermandias/SeaOfStars/main/repo.json";
    const SeaOfStarsStartsWith = "https://raw.githubusercontent.com/Ottermandias/SeaOfStars/";
    const PenumbraInternalName = "Penumbra";
    const HeliosphereInternalName = "heliosphere-plugin";

    let statuses: string[] = $state([]);
    let error: Nullable<string> = $state(undefined);
    let configModified = false;

    onMount(() => {
        canAdvance = false;
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
        const json = await invoke('get_dalamud_config_json') as Nullable<string>;
        if (json == null) {
            error = 'could not read dalamudConfig.json';
            return;
        }

        statuses.push('parsing Dalamud configuration file');
        const config = JSON.parse(json);


        statuses.push('checking for Sea of Stars repository');
        const trl = config['ThirdRepoList'] as any[];
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
            const repoJson = await invoke('create_repo', {
                url: SeaOfStarsRepo,
            }) as Nullable<string>;

            if (repoJson == null) {
                throw new Error('failed to make repo');
            }

            trl.push(JSON.parse(repoJson));
        }

        statuses.push('downloading plugin information from Sea of Stars');
        const resp = await fetch(already || SeaOfStarsRepo);
        const repo = await resp.json() as any[];
        const heliospherePlugin = repo.find(plugin => plugin['InternalName'] === HeliosphereInternalName);
        const penumbraPlugin = repo.find(plugin => plugin['InternalName'] === PenumbraInternalName);

        await installPlugin(penumbraPlugin, config, already || SeaOfStarsRepo);
        await installPlugin(heliospherePlugin, config, already || SeaOfStarsRepo);

        if (configModified) {
            statuses.push('saving Dalamud configuration file');
            const result = await invoke('write_dalamud_config_json', {
                json: JSON.stringify(config, undefined, 4),
            }) as boolean;
            if (!result) {
                error = 'Could not save Dalamud configuration file';
                return;
            }
        }

        // create penumbra config if necessary
        canAdvance = true;
    }

    async function installPlugin(plugin: any, config: any, repoUrl: string): Promise<boolean> {
        const name = plugin['InternalName'];

        statuses.push(`checking if ${name} is already installed`);
        const profile = config['DefaultProfile'];
        if (profile == null) {
            throw new Error('default profile was null');
        }

        const plugins = profile['Plugins'] as Nullable<any[]>;
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

        const workingPluginId = await invoke('install_plugin_from_url', {
            internalName: name,
            url: plugin['DownloadLinkInstall'],
            repoUrl,
        }) as Nullable<string>;

        if (workingPluginId == null) {
            throw new Error('install failed');
        }

        const pluginJson = await invoke('create_plugin', {
            internalName: name,
            workingId: workingPluginId,
        }) as Nullable<string>;

        if (pluginJson == null) {
            throw new Error('could not create plugin');
        }

        plugins.push(JSON.parse(pluginJson));
        return true;
    }
</script>

{#if canAdvance}
    <strong>Installed!</strong>
{:else}
    {#if error != null}
        {error}
    {/if}

    <ul>
        {#each statuses as status}
            <li>{status}</li>
        {/each}
    </ul>
{/if}
