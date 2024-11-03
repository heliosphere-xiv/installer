<script lang="ts">
    import Installer from '$lib/installer.svelte';
    import Prerequisites from '$lib/prerequisites.svelte';
    import Splash from '$lib/splash.svelte';
    import { exit } from '@tauri-apps/plugin-process';

    let step = $state(0);
    let canAdvance = $state(true);
    let canGoBack = $state(true);

    function advanceStep() {
        if (step === 2) {
            exit(0);
            return;
        }

        step += 1;
    }
</script>

<main class='container'>
    <div class='step'>
        {#if step === 0}
            <Splash
                bind:canAdvance={canAdvance}
                onnext={advanceStep}
            />
        {:else if step === 1}
            <Prerequisites
                bind:canAdvance={canAdvance}
            />
        {:else if step === 2}
            <Installer
                bind:canAdvance={canAdvance}
                bind:canGoBack={canGoBack}
            />
        {/if}
    </div>

    {#if step !== 0}
        <div class='step-buttons'>
            <button
                disabled={step === 0 || !canGoBack}
                class='secondary'
                onclick={() => step -= 1}
            >
                Back
            </button>
            <button
                disabled={!canAdvance}
                onclick={advanceStep}
            >
                {#if step === 2}
                    Close
                {:else}
                    Next
                {/if}
            </button>
        </div>
    {/if}

</main>

<style lang='scss'>
    .step-buttons {
        display: flex;
        justify-content: space-between;
        align-items: center;
        width: 100%;
    }

    .container {
        display: flex;
        flex-direction: column;
        align-items: center;
        justify-content: space-between;
        height: 100vh;

        & > .step {
            flex-grow: 1;
        }
    }
</style>
