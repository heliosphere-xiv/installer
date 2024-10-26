<script lang='ts'>
    import { onMount } from 'svelte';
    import type BasicProps from './basicProps';
    import Prerequisite from './prerequisite.svelte';
    import { invoke } from '@tauri-apps/api/core';

    let {
        canAdvance = $bindable(),
    }: BasicProps = $props();

    // let runningPromise: Promise<void> | undefined = undefined;
    let prereqs: [string, boolean | undefined][] = $state([]);

    onMount(() => {
        prereqs = [];
        canAdvance = false;
        // runningPromise = start();
        start();
    });

    async function start() {
        prereqs = [
            ['Checking if FINAL FANTASY XIV is running', undefined],
            ['Checking if XIVLauncher is installed', undefined],
        ];

        await Promise.allSettled([
            checkFfxiv(prereqs, 0),
            checkXl(prereqs, 1),
        ]);

        const allSatisfied = prereqs
            .map(([_, status]) => status)
            .every(status => status === true);
        canAdvance = allSatisfied;
    }

    async function checkFfxiv(reqs: typeof prereqs, idx: number) {
        const running = await invoke('check_for_process', { name: 'ffxiv_dx11' }) as boolean;
        reqs[idx] = [
            running
                ? 'FINAL FANTASY XIV must be closed for the installer to continue'
                : 'FINAL FANTASY XIV is closed',
            !running,
        ];
    }

    async function checkXl(reqs: typeof prereqs, idx: number) {
        const installed = await invoke('dalamud_config_present') as boolean;
        reqs[idx] = [
            installed
                ? 'XIVLauncher is installed'
                : 'XIVLauncher must be installed and launched at least once',
            installed,
        ];
    }
</script>

<h1>Checking prerequisites</h1>

<ul>
    {#each prereqs as [label, state]}
        <Prerequisite
            {label}
            {state}
        />
    {/each}
</ul>

{#if canAdvance}
    <strong>You're all set, click next to begin the installation.</strong>
{/if}
